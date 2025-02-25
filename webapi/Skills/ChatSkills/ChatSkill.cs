﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Azure;
using CopilotChat.WebApi.Extensions;
using CopilotChat.WebApi.Hubs;
using CopilotChat.WebApi.Models.Response;
using CopilotChat.WebApi.Models.Storage;
using CopilotChat.WebApi.Options;
using CopilotChat.WebApi.Services;
using CopilotChat.WebApi.Storage;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.ChatCompletion;
using Microsoft.SemanticKernel.AI.TextCompletion;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;
using Microsoft.SemanticKernel.TemplateEngine.Prompt;
using ChatMessage = CopilotChat.WebApi.Models.Storage.ChatMessage;
using Microsoft.Graph;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace CopilotChat.WebApi.Skills.ChatSkills;

/// <summary>
/// ChatSkill offers a more coherent chat experience by using memories
/// to extract conversation history and user intentions.
/// </summary>
public class ChatSkill
{
    /// <summary>
    /// A kernel instance to create a completion function since each invocation
    /// of the <see cref="ChatAsync"/> function will generate a new prompt dynamically.
    /// </summary>
    private readonly IKernel _kernel;

    /// <summary>
    /// A logger instance to log events.
    /// </summary>
    private ILogger _logger;

    /// <summary>
    /// A repository to save and retrieve chat messages.
    /// </summary>
    private readonly ChatMessageRepository _chatMessageRepository;

    /// <summary>
    /// A repository to save and retrieve chat sessions.
    /// </summary>
    private readonly ChatSessionRepository _chatSessionRepository;

    /// <summary>
    /// A SignalR hub context to broadcast updates of the execution.
    /// </summary>
    private readonly IHubContext<MessageRelayHub> _messageRelayHubContext;

    /// <summary>
    /// Settings containing prompt texts.
    /// </summary>
    private readonly PromptsOptions _promptOptions;

    /// <summary>
    /// A semantic chat memory skill instance to query semantic memories.
    /// </summary>
    private readonly SemanticChatMemorySkill _semanticChatMemorySkill;

    /// <summary>
    /// A document memory skill instance to query document memories.
    /// </summary>
    private readonly DocumentMemorySkill _documentMemorySkill;

    /// <summary>
    /// A skill instance to acquire external information.
    /// </summary>
    private readonly ExternalInformationSkill _externalInformationSkill;

    /// <summary>
    /// Azure content safety moderator.
    /// </summary>
    private readonly AzureContentSafety? _contentSafety = null;

    /// <summary>
    /// Create a new instance of <see cref="ChatSkill"/>.
    /// </summary>
    public ChatSkill(
        IKernel kernel,
        ChatMessageRepository chatMessageRepository,
        ChatSessionRepository chatSessionRepository,
        IHubContext<MessageRelayHub> messageRelayHubContext,
        IOptions<PromptsOptions> promptOptions,
        IOptions<DocumentMemoryOptions> documentImportOptions,
        CopilotChatPlanner planner,
        ILogger logger,
        AzureContentSafety? contentSafety = null)
    {
        this._logger = logger;
        this._kernel = kernel;
        this._chatMessageRepository = chatMessageRepository;
        this._chatSessionRepository = chatSessionRepository;
        this._messageRelayHubContext = messageRelayHubContext;
        // Clone the prompt options to avoid modifying the original prompt options.
        this._promptOptions = promptOptions.Value.Copy();

        this._semanticChatMemorySkill = new SemanticChatMemorySkill(
            promptOptions,
            chatSessionRepository,
            logger);
        this._documentMemorySkill = new DocumentMemorySkill(
            promptOptions,
            documentImportOptions,
            logger);
        this._externalInformationSkill = new ExternalInformationSkill(
            promptOptions,
            planner,
            logger);
        this._contentSafety = contentSafety;
    }

    /// <summary>
    /// Extract user intent from the conversation history.
    /// </summary>
    /// <param name="context">The SKContext.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [SKFunction, Description("Extract user intent")]
    [SKParameter("chatId", "Chat ID to extract history from")]
    [SKParameter("audience", "The audience the chat bot is interacting with.")]
    public async Task<string> ExtractUserIntentAsync(SKContext context, CancellationToken cancellationToken = default)
    {
        var tokenLimit = this._promptOptions.CompletionTokenLimit;
        var historyTokenBudget =
            tokenLimit -
            this._promptOptions.ResponseTokenLimit -
            TokenUtilities.TokenCount(string.Join("\n", new string[]
                {
                    this._promptOptions.SystemDescription,
                    this._promptOptions.SystemIntent,
                    this._promptOptions.SystemIntentContinuation
                })
            );

        // Clone the context to avoid modifying the original context variables.
        var intentExtractionContext = context.Clone();
        intentExtractionContext.Variables.Set("tokenLimit", historyTokenBudget.ToString(new NumberFormatInfo()));
        intentExtractionContext.Variables.Set("knowledgeCutoff", this._promptOptions.KnowledgeCutoffDate);

        var completionFunction = this._kernel.CreateSemanticFunction(
            this._promptOptions.SystemIntentExtraction,
            skillName: nameof(ChatSkill),
            description: "Complete the prompt.");

        var result = await completionFunction.InvokeAsync(
            intentExtractionContext,
            settings: this.CreateIntentCompletionSettings(),
            cancellationToken
        );

        // Get token usage from ChatCompletion result and add to context
        TokenUtilities.GetFunctionTokenUsage(result, context, this._logger, "SystemIntentExtraction");

        result.ThrowIfFailed();

        return $"User intent: {result}";
    }

    /// <summary>
    /// Extract the list of participants from the conversation history.
    /// Note that only those who have spoken will be included.
    /// </summary>
    /// <param name="context">The SKContext.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [SKFunction, Description("Extract audience list")]
    [SKParameter("chatId", "Chat ID to extract history from")]
    public async Task<string> ExtractAudienceAsync(SKContext context, CancellationToken cancellationToken = default)
    {
        var tokenLimit = this._promptOptions.CompletionTokenLimit;
        var historyTokenBudget =
            tokenLimit -
            this._promptOptions.ResponseTokenLimit -
            TokenUtilities.TokenCount(string.Join("\n", new string[]
                {
                    this._promptOptions.SystemAudience,
                    this._promptOptions.SystemAudienceContinuation,
                })
            );

        // Clone the context to avoid modifying the original context variables.
        var audienceExtractionContext = context.Clone();
        audienceExtractionContext.Variables.Set("tokenLimit", historyTokenBudget.ToString(new NumberFormatInfo()));

        var completionFunction = this._kernel.CreateSemanticFunction(
            this._promptOptions.SystemAudienceExtraction,
            skillName: nameof(ChatSkill),
            description: "Complete the prompt.");

        var result = await completionFunction.InvokeAsync(
            audienceExtractionContext,
            settings: this.CreateIntentCompletionSettings(),
            cancellationToken
        );

        // Get token usage from ChatCompletion result and add to context
        TokenUtilities.GetFunctionTokenUsage(result, context, this._logger, "SystemAudienceExtraction");

        result.ThrowIfFailed();

        return $"List of participants: {result}";
    }

    /// <summary>
    /// Extract chat history.
    /// </summary>
    /// <param name="context">Contains the 'tokenLimit' controlling the length of the prompt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [SKFunction, Description("Extract chat history")]
    public async Task<string> ExtractChatHistoryAsync(
        [Description("Chat ID to extract history from")] string chatId,
        [Description("Maximum number of tokens")] int tokenLimit,
        CancellationToken cancellationToken = default)
    {
        var messages = await this._chatMessageRepository.FindByChatIdAsync(chatId);
        var sortedMessages = messages.OrderByDescending(m => m.Timestamp);

        var remainingToken = tokenLimit;

        string historyText = string.Empty;
        foreach (var chatMessage in sortedMessages)
        {
            var formattedMessage = chatMessage.ToFormattedString();

            // Plan object is not meaningful content in generating bot response, so shorten to intent only to save on tokens
            if (formattedMessage.Contains("proposedPlan\":", StringComparison.InvariantCultureIgnoreCase))
            {
                string pattern = @"(\[.*?\]).*User Intent:User intent: (.*)(?=""}})";
                Match match = Regex.Match(formattedMessage, pattern);
                if (match.Success)
                {
                    string timestamp = match.Groups[1].Value.Trim();
                    string userIntent = match.Groups[2].Value.Trim();

                    formattedMessage = $"{timestamp} Bot proposed plan to fulfill user intent: {userIntent}";
                }
                else
                {
                    formattedMessage = "Bot proposed plan";
                }
            }

            var tokenCount = TokenUtilities.TokenCount(formattedMessage);

            if (remainingToken - tokenCount >= 0)
            {
                historyText = $"{formattedMessage}\n{historyText}";
                remainingToken -= tokenCount;
            }
            else
            {
                break;
            }
        }

        return $"Chat history:\n{historyText.Trim()}";
    }

    /// <summary>
    /// This is the entry point for getting a chat response. It manages the token limit, saves
    /// messages to memory, and fill in the necessary context variables for completing the
    /// prompt that will be rendered by the template engine.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    [SKFunction, Description("Get chat response")]
    public async Task<SKContext> ChatAsync(
        [Description("The new message")] string message,
        [Description("Unique and persistent identifier for the user")] string userId,
        [Description("Name of the user")] string userName,
        [Description("Unique and persistent identifier for the chat")] string chatId,
        [Description("Type of the message")] string messageType,
        [Description("Previously proposed plan that is approved"), DefaultValue(null), SKName("proposedPlan")] string? planJson,
        [Description("ID of the response message for planner"), DefaultValue(null), SKName("responseMessageId")] string? messageId,
        SKContext context,
        CancellationToken cancellationToken = default)
    {
        // Set the system description in the prompt options
        await this.SetSystemDescriptionAsync(chatId, cancellationToken);

        // Save this new message to memory such that subsequent chat responses can use it
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Saving user message to chat history", cancellationToken);
        await this.SaveNewMessageAsync(message, userId, userName, chatId, messageType, cancellationToken);

        // Clone the context to avoid modifying the original context variables.
        var chatContext = context.Clone();
        chatContext.Variables.Set("knowledgeCutoff", this._promptOptions.KnowledgeCutoffDate);

        // Check if plan exists in ask's context variables.
        // If plan was returned at this point, that means it was approved or cancelled.
        // Update the response previously saved in chat history with state
        if (!string.IsNullOrWhiteSpace(planJson) &&
            !string.IsNullOrEmpty(messageId))
        {
            await this.UpdateChatMessageContentAsync(planJson, messageId, cancellationToken);
        }

        ChatMessage chatMessage;
        if (chatContext.Variables.ContainsKey("userCancelledPlan"))
        {
            // Save hardcoded response if user cancelled plan
            chatMessage = await this.SaveNewResponseAsync("I am sorry the plan did not meet your goals.", string.Empty, chatId, userId, TokenUtilities.EmptyTokenUsages(), cancellationToken);
        }
        else
        {
            // Get the chat response
            chatMessage = await this.GetChatResponseAsync(chatId, userId, chatContext, cancellationToken);
        }

        context.Variables.Update(chatMessage.Content);

        if (chatMessage.TokenUsage != null)
        {
            context.Variables.Set("tokenUsage", JsonSerializer.Serialize(chatMessage.TokenUsage));
        }
        else
        {
            this._logger.LogWarning("ChatSkill token usage unknown. Ensure token management has been implemented correctly.");
        }

        return context;
    }

    #region Private

    /// <summary>
    /// Generate the necessary chat context to create a prompt then invoke the model to get a response.
    /// </summary>
    /// <param name="chatId">The chat ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="chatContext">The SKContext.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created chat message containing the model-generated response.</returns>
    private async Task<ChatMessage> GetChatResponseAsync(string chatId, string userId, SKContext chatContext, CancellationToken cancellationToken)
    {
        // Get the audience
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Extracting audience", cancellationToken);
        var audience = await this.GetAudienceAsync(chatContext, cancellationToken);
        chatContext.ThrowIfFailed();

        // Extract user intent from the conversation history.
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Extracting user intent", cancellationToken);
        var userIntent = await this.GetUserIntentAsync(chatContext, cancellationToken);
        chatContext.ThrowIfFailed();

        chatContext.Variables.Set("audience", audience);
        chatContext.Variables.Set("userIntent", userIntent);

        // Calculate the remaining token budget.
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Calculating remaining token budget", cancellationToken);
        var remainingToken = this.GetChatContextTokenLimit(audience, userIntent);

        // Acquire external information from planner
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Acquiring external information from planner", cancellationToken);
        var externalInformationTokenLimit = (int)(remainingToken * this._promptOptions.ExternalInformationContextWeight);
        var planResult = await this.AcquireExternalInformationAsync(chatContext, userIntent, externalInformationTokenLimit, cancellationToken);
        chatContext.ThrowIfFailed();

        // Extract additional details about planner execution in chat context
        // TODO: [Issue #150, sk#2106] Accommodate different planner contexts once core team finishes work to return prompt and token usage.
        var plannerDetails = new SemanticDependency<StepwiseThoughtProcess>(
                planResult,
                this._externalInformationSkill.StepwiseThoughtProcess
            );

        // If plan is suggested, send back to user for approval before running
        var proposedPlan = this._externalInformationSkill.ProposedPlan;
        if (proposedPlan != null)
        {
            var prompt = proposedPlan.Plan.Description;
            chatContext.Variables.Set("prompt", prompt);

            // Save a new response to the chat history with the proposed plan content
            return await this.SaveNewResponseAsync(
                JsonSerializer.Serialize<ProposedPlan>(proposedPlan),
                prompt,
                chatId,
                userId,
                // TODO: [Issue #2106] Accommodate plan token usage differently
                this.GetTokenUsages(chatContext),
                cancellationToken
            );
        }

        // Query relevant semantic and document memories
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Extracting semantic and document memories", cancellationToken);
        var chatMemoriesTokenLimit = (int)(remainingToken * this._promptOptions.MemoriesResponseContextWeight);
        var documentContextTokenLimit = (int)(remainingToken * this._promptOptions.DocumentContextWeight);

        string[] tasks = await Task.WhenAll<string>(
            this._semanticChatMemorySkill.QueryMemoriesAsync(userIntent, chatId, chatMemoriesTokenLimit, this._kernel.Memory, cancellationToken),
            this._documentMemorySkill.QueryDocumentsAsync(userIntent, chatId, documentContextTokenLimit, this._kernel.Memory, cancellationToken)
        );

        var chatMemories = tasks[0];
        var documentMemories = tasks[1];

        // Fill in the chat history if there is any token budget left
        var chatContextComponents = new List<string>() { chatMemories, documentMemories };
        var chatContextText = string.Join("\n\n", chatContextComponents.Where(c => !string.IsNullOrEmpty(c)));
        var chatHistoryTokenLimit = remainingToken - TokenUtilities.TokenCount(chatContextText) - TokenUtilities.TokenCount(planResult);

        string chatHistory = string.Empty;

        // Append the chat history, if allowed.
        if (chatHistoryTokenLimit > 0)
        {
            await this.UpdateBotResponseStatusOnClientAsync(chatId, "Extracting chat history", cancellationToken);
            chatHistory = await this.ExtractChatHistoryAsync(chatId, chatHistoryTokenLimit, cancellationToken);
            chatContext.ThrowIfFailed();
            chatContextText = $"{chatContextText}\n{chatHistory}";
        }

        // Append the plan result last, if exists, to imply precedence.
        if (!string.IsNullOrWhiteSpace(planResult))
        {
            chatContextText = $"{chatContextText}\n{planResult}";
        }

        // Set variables needed in prompt
        chatContext.Variables.Set("audience", audience);
        chatContext.Variables.Set("userIntent", userIntent);
        chatContext.Variables.Set("chatContext", chatContextText);

        // Render the prompt
        var promptRenderer = new PromptTemplateEngine();
        var renderedPrompt = await promptRenderer.RenderAsync(
            this._promptOptions.SystemChatPrompt,
            chatContext,
            cancellationToken);
        chatContext.Variables.Set("prompt", renderedPrompt);

        // Need to extract this from the rendered prompt because Time and Date are calculated during render
        var systemChatContinuation = Regex.Match(renderedPrompt, PromptsOptions.SYSTEM_CHAT_CONTINUATION_REGEX).Value;
        var promptView = new BotResponsePrompt(renderedPrompt, this._promptOptions.SystemDescription, this._promptOptions.SystemResponse, audience, userIntent, chatMemories, documentMemories, plannerDetails, chatHistory, systemChatContinuation);

        // Calculate token usage of prompt template
        chatContext.Variables.Set(TokenUtilities.GetFunctionKey(this._logger, "SystemMetaPrompt")!, TokenUtilities.TokenCount(renderedPrompt).ToString(CultureInfo.InvariantCulture));

        chatContext.ThrowIfFailed();

        // Stream the response to the client
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Generating bot response", cancellationToken);
        var chatMessage = await this.StreamResponseToClientAsync(chatId, userId, promptView, cancellationToken);
        // Extract semantic chat memory
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Generating semantic chat memory", cancellationToken);
        await SemanticChatMemoryExtractor.ExtractSemanticChatMemoryAsync(
            chatId,
            this._kernel,
            chatContext,
            this._promptOptions,
            this._logger,
            cancellationToken);

        // Calculate total token usage for dependency functions and prompt template and send to client
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Calculating token usage", cancellationToken);
        chatMessage.TokenUsage = this.GetTokenUsages(chatContext, chatMessage.Content);
        await this.UpdateMessageOnClient(chatMessage, cancellationToken);

        // Save the message with final completion token usage
        await this.UpdateBotResponseStatusOnClientAsync(chatId, "Saving message to chat history", cancellationToken);
        await this._chatMessageRepository.UpsertAsync(chatMessage);

        return chatMessage;
    }

    /// <summary>
    /// Helper function that creates the correct context variables to
    /// extract the audience from a conversation history.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<string> GetAudienceAsync(SKContext context, CancellationToken cancellationToken)
    {
        SKContext audienceContext = context.Clone();

        var audience = await this.ExtractAudienceAsync(audienceContext, cancellationToken);

        // Copy token usage into original chat context
        var functionKey = TokenUtilities.GetFunctionKey(this._logger, "SystemAudienceExtraction")!;
        if (audienceContext.Variables.TryGetValue(functionKey, out string? tokenUsage))
        {
            context.Variables.Set(functionKey, tokenUsage);
        }

        // Propagate the error
        audienceContext.ThrowIfFailed();

        return audience;
    }

    /// <summary>
    /// Helper function that creates the correct context variables to
    /// extract the user intent from the conversation history.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<string> GetUserIntentAsync(SKContext context, CancellationToken cancellationToken)
    {
        // TODO: [Issue #51] Regenerate user intent if plan was modified
        if (!context.Variables.TryGetValue("planUserIntent", out string? userIntent))
        {
            SKContext intentContext = context.Clone();

            userIntent = await this.ExtractUserIntentAsync(intentContext, cancellationToken);

            // Copy token usage into original chat context
            var functionKey = TokenUtilities.GetFunctionKey(this._logger, "SystemIntentExtraction")!;
            if (intentContext.Variables.TryGetValue(functionKey!, out string? tokenUsage))
            {
                context.Variables.Set(functionKey!, tokenUsage);
            }

            intentContext.ThrowIfFailed();
        }

        return userIntent;
    }

    /// <summary>
    /// Helper function that queries chat memories from the chat memory store.
    /// </summary>
    /// <returns>The chat memories.</returns>
    /// <param name="context">The SKContext.</param>
    /// <param name="userIntent">The user intent.</param>
    /// <param name="tokenLimit">Maximum number of tokens.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private Task<string> QueryChatMemoriesAsync(SKContext context, string userIntent, int tokenLimit, CancellationToken cancellationToken)
    {
        return this._semanticChatMemorySkill.QueryMemoriesAsync(userIntent, context.Variables["chatId"], tokenLimit, this._kernel.Memory, cancellationToken);
    }

    /// <summary>
    /// Helper function that queries document memories from the document memory store.
    /// </summary>
    /// <returns>The document memories.</returns>
    /// <param name="context">The SKContext.</param>
    /// <param name="userIntent">The user intent.</param>
    /// <param name="tokenLimit">Maximum number of tokens.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private Task<string> QueryDocumentsAsync(SKContext context, string userIntent, int tokenLimit, CancellationToken cancellationToken)
    {
        return this._documentMemorySkill.QueryDocumentsAsync(userIntent, context.Variables["chatId"], tokenLimit, this._kernel.Memory, cancellationToken);
    }

    /// <summary>
    /// Helper function that creates the correct context variables to acquire external information.
    /// </summary>
    /// <returns>The plan.</returns>
    /// <param name="context">The SKContext.</param>
    /// <param name="userIntent">The user intent.</param>
    /// <param name="tokenLimit">Maximum number of tokens.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<string> AcquireExternalInformationAsync(SKContext context, string userIntent, int tokenLimit, CancellationToken cancellationToken)
    {
        SKContext planContext = context.Clone();
        planContext.Variables.Set("tokenLimit", tokenLimit.ToString(new NumberFormatInfo()));

        var plan = await this._externalInformationSkill.AcquireExternalInformationAsync(userIntent, planContext, cancellationToken);

        // Propagate the error
        planContext.ThrowIfFailed();

        return plan;
    }

    /// <summary>
    /// Save a new message to the chat history.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="userId">The user ID</param>
    /// <param name="userName"></param>
    /// <param name="chatId">The chat ID</param>
    /// <param name="type">Type of the message</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<ChatMessage> SaveNewMessageAsync(string message, string userId, string userName, string chatId, string type, CancellationToken cancellationToken)
    {
        // Make sure the chat exists.
        if (!await this._chatSessionRepository.TryFindByIdAsync(chatId))
        {
            throw new ArgumentException("Chat session does not exist.");
        }

        var chatMessage = new ChatMessage(
            userId,
            userName,
            chatId,
            message,
            string.Empty,
            ChatMessage.AuthorRoles.User,
            // Default to a standard message if the `type` is not recognized
            Enum.TryParse(type, out ChatMessage.ChatMessageType typeAsEnum) && Enum.IsDefined(typeof(ChatMessage.ChatMessageType), typeAsEnum)
                ? typeAsEnum
                : ChatMessage.ChatMessageType.Message);

        await this._chatMessageRepository.CreateAsync(chatMessage);
        return chatMessage;
    }

    /// <summary>
    /// Save a new response to the chat history.
    /// </summary>
    /// <param name="response">Response from the chat.</param>
    /// <param name="prompt">Prompt used to generate the response.</param>
    /// <param name="chatId">The chat ID</param>
    /// <param name="userId">The user ID</param>
    ///  <param name="tokenUsage">Total token usage of response completion</param>
    ///  <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created chat message.</returns>
    private async Task<ChatMessage> SaveNewResponseAsync(string response, string prompt, string chatId, string userId, Dictionary<string, int>? tokenUsage, CancellationToken cancellationToken)
    {
        // Make sure the chat exists.
        if (!await this._chatSessionRepository.TryFindByIdAsync(chatId))
        {
            throw new ArgumentException("Chat session does not exist.");
        }

        // Save message to chat history
        var chatMessage = await this.CreateBotMessageOnClient(chatId, userId, prompt, response, cancellationToken, tokenUsage);
        await this._chatMessageRepository.UpsertAsync(chatMessage);
        return chatMessage;
    }

    /// <summary>
    /// Updates previously saved response in the chat history.
    /// </summary>
    /// <param name="updatedResponse">Updated response from the chat.</param>
    /// <param name="messageId">The chat message ID</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task UpdateChatMessageContentAsync(string updatedResponse, string messageId, CancellationToken cancellationToken)
    {
        // Make sure the chat exists.
        var chatMessage = await this._chatMessageRepository.FindByIdAsync(messageId);
        chatMessage.Content = updatedResponse;

        await this._chatMessageRepository.UpsertAsync(chatMessage);
    }

    /// <summary>
    /// Create `ChatRequestSettings` for chat response. Parameters are read from the PromptSettings class.
    /// </summary>
    private ChatRequestSettings CreateChatRequestSettings()
    {
        return new ChatRequestSettings
        {
            MaxTokens = this._promptOptions.ResponseTokenLimit,
            Temperature = this._promptOptions.ResponseTemperature,
            TopP = this._promptOptions.ResponseTopP,
            FrequencyPenalty = this._promptOptions.ResponseFrequencyPenalty,
            PresencePenalty = this._promptOptions.ResponsePresencePenalty
        };
    }

    /// <summary>
    /// Create `CompleteRequestSettings` for intent response. Parameters are read from the PromptSettings class.
    /// </summary>
    private CompleteRequestSettings CreateIntentCompletionSettings()
    {
        return new CompleteRequestSettings
        {
            MaxTokens = this._promptOptions.ResponseTokenLimit,
            Temperature = this._promptOptions.IntentTemperature,
            TopP = this._promptOptions.IntentTopP,
            FrequencyPenalty = this._promptOptions.IntentFrequencyPenalty,
            PresencePenalty = this._promptOptions.IntentPresencePenalty,
            StopSequences = new string[] { "] bot:" }
        };
    }

    /// <summary>
    /// Calculate the remaining token budget for the chat response prompt.
    /// This is the token limit minus the token count of the user intent and the system commands.
    /// </summary>
    /// <param name="audience">The audience returned by the model.</param>
    /// <param name="userIntent">The user intent returned by the model.</param>
    /// <returns>The remaining token limit.</returns>
    private int GetChatContextTokenLimit(string audience, string userIntent)
    {
        var tokenLimit = this._promptOptions.CompletionTokenLimit;
        var remainingToken =
            tokenLimit -
            TokenUtilities.TokenCount(audience) -
            TokenUtilities.TokenCount(userIntent) -
            this._promptOptions.ResponseTokenLimit -
            TokenUtilities.TokenCount(string.Join("\n", new string[]
                {
                            this._promptOptions.SystemDescription,
                            this._promptOptions.SystemResponse,
                            this._promptOptions.SystemChatContinuation
                })
            );

        return remainingToken;
    }

    /// <summary>
    /// Gets token usage totals for each semantic function if not undefined.
    /// </summary>
    /// <param name="chatContext">Context maintained during response generation.</param>
    /// <param name="content">String representing bot response. If null, response is still being generated or was hardcoded.</param>
    /// <returns>Dictionary containing function to token usage mapping for each total that's defined.</returns>
    private Dictionary<string, int> GetTokenUsages(SKContext chatContext, string? content = null)
    {
        var tokenUsageDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Total token usage of each semantic function
        foreach (string function in TokenUtilities.semanticFunctions.Values)
        {
            if (chatContext.Variables.TryGetValue($"{function}TokenUsage", out string? tokenUsage))
            {
                tokenUsageDict.Add(function, int.Parse(tokenUsage, CultureInfo.InvariantCulture));
            }
        }

        if (content != null)
        {
            tokenUsageDict.Add(TokenUtilities.semanticFunctions["SystemCompletion"]!, TokenUtilities.TokenCount(content));
        }

        return tokenUsageDict;
    }

    /// <summary>
    /// Stream the response to the client.
    /// </summary>
    /// <param name="chatId">The chat ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="prompt">Prompt used to generate the response</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created chat message</returns>
    private async Task<ChatMessage> StreamResponseToClientAsync(string chatId, string userId, BotResponsePrompt prompt, CancellationToken cancellationToken)
    {
        // Create the stream
        var chatCompletion = this._kernel.GetService<IChatCompletion>();
        var stream = chatCompletion.GenerateMessageStreamAsync(chatCompletion.CreateNewChat(prompt.RawContent), this.CreateChatRequestSettings(), cancellationToken);

        // Create message on client
        var chatMessage = await this.CreateBotMessageOnClient(chatId, userId, JsonSerializer.Serialize(prompt), string.Empty, cancellationToken);

        // Stream the message to the client
        await foreach (string contentPiece in stream)
        {
            chatMessage.Content += contentPiece;
            await this.UpdateMessageOnClient(chatMessage, cancellationToken);
        }

        OpenAIClient client = new(new Uri("https://openai1234e.openai.azure.com/"), new AzureKeyCredential("6d78704dac3c45e1a8d73381ee33c7ac"));

        if ((chatMessage.Content.Contains("uri", StringComparison.OrdinalIgnoreCase)
            || chatMessage.Content.Contains("image", StringComparison.OrdinalIgnoreCase)
            || chatMessage.Content.Contains("photo", StringComparison.OrdinalIgnoreCase)))
        {
            
            Response<ImageGenerations> imageGenerations = await client.GetImageGenerationsAsync(
                          new ImageGenerationOptions()
                          {
                              Prompt = chatMessage.Content,
                              Size = ImageSize.Size256x256,
                          });
            Uri imageUri = imageGenerations.Value.Data[0].Url;

            chatMessage.Content += imageUri.ToString();
            await this.UpdateMessageOnClient(chatMessage, cancellationToken);
        }
        
        

        return chatMessage;
    }

    /// <summary>
    /// Create an empty message on the client to begin the response.
    /// </summary>
    /// <param name="chatId">The chat ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="prompt">Prompt used to generate the message</param>
    /// <param name="content">Content of the message</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="tokenUsage">Total token usage of response completion</param>
    /// <returns>The created chat message</returns>
    private async Task<ChatMessage> CreateBotMessageOnClient(string chatId, string userId, string prompt, string content, CancellationToken cancellationToken, Dictionary<string, int>? tokenUsage = null)
    {
        var chatMessage = ChatMessage.CreateBotResponseMessage(chatId, content, prompt, tokenUsage);
        await this._messageRelayHubContext.Clients.Group(chatId).SendAsync("ReceiveMessage", chatId, userId, chatMessage, cancellationToken);
        return chatMessage;
    }

    /// <summary>
    /// Update the response on the client.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task UpdateMessageOnClient(ChatMessage message, CancellationToken cancellationToken)
    {
        await this._messageRelayHubContext.Clients.Group(message.ChatId).SendAsync("ReceiveMessageUpdate", message, cancellationToken);
    }

    /// <summary>
    /// Update the status of the response on the client.
    /// </summary>
    /// <param name="chatId">The chat ID</param>
    /// <param name="status">Current status of the response</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task UpdateBotResponseStatusOnClientAsync(string chatId, string status, CancellationToken cancellationToken)
    {
        await this._messageRelayHubContext.Clients.Group(chatId).SendAsync("ReceiveBotResponseStatus", chatId, status, cancellationToken);
    }

    /// <summary>
    /// Set the system description in the prompt options.
    /// </summary>
    /// <param name="chatId">Id of the chat session</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ArgumentException">Throw if the chat session does not exist.</exception>
    private async Task SetSystemDescriptionAsync(string chatId, CancellationToken cancellationToken)
    {
        ChatSession? chatSession = null;
        if (!await this._chatSessionRepository.TryFindByIdAsync(chatId, callback: v => chatSession = v))
        {
            throw new ArgumentException("Chat session does not exist.");
        }

        this._promptOptions.SystemDescription = chatSession!.SystemDescription;
    }

    # endregion
}
