using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Rhino.AI.Models;
using Rhino.AI.PlugIns;

namespace Rhino.AI;

/// <summary>
/// The Agent is the controller of all the AI parts.
/// </summary>
public sealed class Agent(PlugInToken token, IModel model, IHarness harness, string defaultPrompt = "")
{

    private PlugInToken Token { get; } = token;

    private List<ITurn> PrivateTurns { get; } = [];

    /// <summary>
    /// All turns in the conversation thus far
    /// </summary>
    public IReadOnlyList<ITurn> Turns => PrivateTurns;

    /// <summary>
    /// The Model in use by the agent
    /// </summary>
    public IModel Model { get; } = model;

    /// <summary>
    /// The current <see cref="IHarness"/>
    /// </summary>
    public IHarness Harness { get; } = harness;

    /// <summary>
    /// The default prompt of the agent
    /// </summary>
    public string DefaultPrompt { get; } = defaultPrompt;

    /// <summary>
    /// Configuration
    /// </summary>
    public AgentConfig Config { get; } = new();

    // TODO : Enum ??
    // public string Effort { get; set; }

    /// <summary>
    /// Forks an agent at a point in a conversation.
    /// </summary>
    /// <returns>A forked copy of an Agent with all of the state copied safely.</returns>
    internal Agent Fork() => WithNewModel(Model);

    /// <summary>
    /// Create a new Agent with a new model
    /// </summary>
    /// <param name="model"></param>
    /// <returns>A freshly made agent with all of the state copied safely</returns>
    internal Agent WithNewModel(IModel model)
    {
        // TODO : Desktop models are messy here
        // if (model is DesktopModel desktopModel)
        Agent agent = new(Token, model, Harness, DefaultPrompt);
        agent.PrivateTurns.AddRange(PrivateTurns.Select(t => t.Copy()));
        AgentConfig.Push(this.Config, agent.Config);

        return agent;
    }

    private SemaphoreSlim TurnGate { get; } = new(1, 1);

    /// <summary>
    /// Sends a message to the assigned model
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>Each <see cref="ITurn"/> as the Harness loop produces it</returns>
    /// <exception cref="InvalidOperationException">Calls to this method may not run simultaneously for the same agent</exception>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public IAsyncEnumerable<ITurn> StreamAsync(string message, CancellationToken token)
        => StreamAsync([new TextContent(message)], token);

    /// <summary>
    /// Sends content to the assigned model
    /// </summary>
    /// <param name="content">The content to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>Each <see cref="ITurn"/> as the Harness loop produces it</returns>
    /// <exception cref="InvalidOperationException">Calls to this method may not run simultaneously for the same agent</exception>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public IAsyncEnumerable<ITurn> StreamAsync(IMessageContent content, CancellationToken token)
        => StreamAsync([content], token);

    /// <summary>
    /// Sends content to the assigned model
    /// </summary>
    /// <param name="contents">The content parts of the message to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>Each <see cref="ITurn"/> as the Harness loop produces it</returns>
    /// <exception cref="InvalidOperationException">Calls to this method may not run simultaneously for the same agent</exception>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public async IAsyncEnumerable<ITurn> StreamAsync(IEnumerable<IMessageContent> contents, [EnumeratorCancellation] CancellationToken token)
    {
        if (!TurnGate.Wait(0, CancellationToken.None))
            throw new InvalidOperationException($"{nameof(StreamAsync)} cannot be run until the previous call has finished on the same agent.");

        try
        {
            if (!PlugInRegistry.HasPermission(Token))
                throw new PermissionException($"PlugIn {Token.Name} does not have permission");

            if (!UserPermissions.IsPermitted(Model.Vendor, Model.Name))
                throw new PermissionException("Model or Vendor does not have permission.");

            List<ITurn> newTurns = [];

            if (PrivateTurns.Count == 0)
            {
                if (!string.IsNullOrEmpty(DefaultPrompt))
                    newTurns.Add(new SystemTurn(DefaultPrompt));

                if (SkillsPrompt(Harness.Skills) is string skillsPrompt)
                    newTurns.Add(new SystemTurn(skillsPrompt));
            }

            newTurns.Add(new MessageTurn(contents, RoleType.User));

            PrivateTurns.AddRange(newTurns);

            foreach (ITurn turn in newTurns)
                yield return turn;

            await foreach (ITurn turn in Harness.StreamLoopAsync(this, [.. PrivateTurns], token).ConfigureAwait(false))
            {
                Statistics.PlugInStatistics.Collect(this, Token, [turn]);
                PrivateTurns.Add(turn);
                yield return turn;
            }
        }
        finally
        {
            TurnGate.Release();
        }
    }

    /// <summary>
    /// Sends a message to the assigned model
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>The resulting <see cref="ITurn"/>s created within the Harness loop once finished</returns>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public async Task<IEnumerable<ITurn>> SendAsync(string message, CancellationToken token)
        => await SendAsync([new TextContent(message)], token);

    /// <summary>
    /// Sends content to the assigned model
    /// </summary>
    /// <param name="content">The content to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>The resulting <see cref="ITurn"/>s created within the Harness loop once finished</returns>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public async Task<IEnumerable<ITurn>> SendAsync(IMessageContent content, CancellationToken token)
        => await SendAsync([content], token);

    /// <summary>
    /// Sends content to the assigned model
    /// </summary>
    /// <param name="contents">The contents to send</param>
    /// <param name="token">A cancellation token</param>
    /// <returns>The resulting <see cref="ITurn"/>s created within the Harness loop once finished</returns>
    /// <exception cref="PermissionException">If the requested model or vendor is not allowed an exception will be raised</exception>
    public async Task<IEnumerable<ITurn>> SendAsync(IEnumerable<IMessageContent> contents, CancellationToken token)
    {
        List<ITurn> turns = [];
        await foreach (ITurn turn in StreamAsync(contents, token).ConfigureAwait(false))
        {
            turns.Add(turn);
        }

        return turns;
    }

    private string? SkillsPrompt(IReadOnlyDictionary<string, ISkill> skills)
    {
        if (skills.Count == 0) return null;

        // If read_skill is not available, return null.
        if (!Harness.Mcps.Values.Any(m => m.Tools.ContainsKey("read_skill"))) return null;

        StringBuilder prompt = new("The following skills are available. Call read_skill with a skill's name to load its full instructions before using it.\n");
        foreach (ISkill skill in skills.Values)
            prompt.Append($"\n- {skill.Name}: {skill.Description}");

        return prompt.ToString();
    }

    // TODO : Should there be calls like GET CLAUDE DESKTOP or nah? I feel like the GetPreferredAgent is best

    /// <summary>
    /// Returns a claude desktop agent that uses the desktop harness
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetClaudeDesktopAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("Anthropic", AgentType.Cli)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a CodexDesktop agent that uses a <see cref="CodexHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetCodexDesktopAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("OpenAI", AgentType.Cli)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a Claude agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetClaudeAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("Anthropic", AgentType.Api)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a ChatGPT agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetChatGptAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("OpenAI", AgentType.Api)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a Gemini agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetGeminiAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("Google", AgentType.Api)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a DeepSeek agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetDeepSeekAgent(PlugIns.PlugInToken token, string model, string prompt)
        => Instances.Agents.FromParameters("DeepSeek", AgentType.Api)?.GetAgent(token, model, prompt);

    /// <summary>
    /// Returns a LMStudio agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="vendor">The AI Model vendor</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetLocalAgent(PlugIns.PlugInToken token, string vendor, string model, string prompt)
        => Instances.Agents.FromParameters(vendor, AgentType.Local)?.GetAgent(token, model, prompt);
    
    /// <summary>
    /// Returns a LMStudio agent that uses a <see cref="GenericHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="protocol">The Protocl method the model uses to communicate</param>
    /// <param name="model">The AI Model</param>
    /// <param name="vendor">The AI Vendor</param>
    /// <param name="server">The server of the local model. Usually http://localhost:port</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? GetCustomAgent(PlugIns.PlugInToken token, ProtocolKind protocol, string model, string vendor, Uri server, string prompt)
    {
        // TODO : throw may be better?
        IModel? modelInstance = protocol switch
        {
            ProtocolKind.Messages => UnknownModel.Anthropic(model, vendor, server),
            ProtocolKind.Completions => UnknownModel.Completions(model, vendor, server),
            ProtocolKind.Responses => UnknownModel.Responses(model, vendor, server),

            _ => null
        };
        if (modelInstance is null) return null;
        // TODO : ?? Or not ? => Instances.Agents.FromParameters("OpenAI", false)?.GetAgent(token, model, prompt);
        return new(token, modelInstance, new GenericHarness(token), prompt);
    }

    /// <summary>
    /// Returns the users prefered agent, harness, and overall config
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="prompt">The starting prompt</param>
    /// <returns>An Agent</returns>
    public static Agent? GetPreferredAgent(PlugIns.PlugInToken token, string prompt)
        => Instances.Agents.PreferredDefinition?.GetAgent(token, prompt);

    /// <summary>
    /// Returns an Agent that uses the appropriate <see cref="IHarness"/>
    /// </summary>
    /// <param name="token">The permissions token</param>
    /// <param name="model">The AI Model</param>
    /// <param name="prompt">The default prompt</param>
    /// <returns>An Agent</returns>
    internal static Agent? FromModel(PlugInToken token, IModel model, string prompt) => model switch
    {
        ClaudeDesktopModel => GetClaudeDesktopAgent(token, model.Name, prompt),
        CodexDesktopModel => GetCodexDesktopAgent(token, model.Name, prompt),

        // TODO : fallthrough might be sufficient
        DeepSeekModel => GetDeepSeekAgent(token, model.Name, prompt),
        ClaudeModel => GetClaudeAgent(token, model.Name, prompt),
        ChatGptModel => GetChatGptAgent(token, model.Name, prompt),
        GeminiModel => GetGeminiAgent(token, model.Name, prompt),

        LocalModel => GetLocalAgent(token, model.Vendor, model.Name, prompt),

        _ => new Agent(token, model, new GenericHarness(token), prompt),
    };

    /// <summary>
    /// Empties all previous turns
    /// </summary>
    internal void ClearConversation()
    {
        PrivateTurns.Clear();
    }

}

/// <summary>
/// Agent Configuration
/// </summary>
public sealed record AgentConfig
{

    /// <summary>
    /// Uses the local settings of the Agents Harness
    /// </summary>
    public bool UseLocalSettings { get; set; } = false;

    /// <summary>
    /// The Session Id to resume with
    /// </summary>
    public Guid SessionId { get; set; } = Guid.Empty;

    /// <summary>
    /// Pushes Config from one agent to another
    /// </summary>
    public static void Push(AgentConfig from, AgentConfig to)
    {
        to.UseLocalSettings = from.UseLocalSettings;
        to.SessionId = from.SessionId;
    }

}
