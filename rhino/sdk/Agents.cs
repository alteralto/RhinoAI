using System;
using System.Linq;
using System.Collections.Generic;

using Rhino.AI.Models;
using System.Runtime.InteropServices;

namespace Rhino.AI;

/// <summary>
/// Storage for the current Agents
/// </summary>
internal sealed class Agents
{

    private Dictionary<Guid, IAgentDefinition> PrivateDefinitions { get; } = [];
    public IReadOnlyCollection<IAgentDefinition> Definitions => PrivateDefinitions.Values;

    public IAgentDefinition? PreferredDefinition
        => PrivateDefinitions.TryGetValue(UserPreferences.AgentId, out IAgentDefinition? def) ? def : null;

    public bool AgentDefinitions(IAgentDefinition definition)
        => PrivateDefinitions.TryAdd(definition.Id, definition);

    private bool LoadAgentsFromJson(string json)
    {
        try
        {
            // TODO : Get Claude to write this
            // See : Definitions.json
            return true;
        }
        catch
        {
            return false;
        }
    }

    // NOTE : These are ONLY ones used for the AI Panel
    private Dictionary<uint, List<Agent>> ActiveAgents { get; } = [];

    public bool TryRegisterAgent(uint id, Agent agent)
    {
        ActiveAgents.TryGetValue(id, out List<Agent>? agents);
        agents ??= new();
        agents.Add(agent);
        ActiveAgents[id] = agents;

        return true;
    }

    public IAgentDefinition? FromParameters(string vendor, AgentType type)
    {
        foreach(IAgentDefinition def in Definitions)
        {
            if (!string.Equals(def.Vendor, vendor, StringComparison.OrdinalIgnoreCase)) continue;
            if (type == AgentType.Cli && def is not CliAgentDefinition) continue;
            if (type == AgentType.Api && def is not HttpAgentDefinition) continue;
            if (type == AgentType.Local && def is not HttpAgentDefinition) continue;
            return def;
        }

        return null;
    }

}

internal enum AgentType { Cli, Api, Local }

// NOTE : This, as a contract, feels correct. 
//        A User will want to create and modify definitions. Maybe a user wants TWO Claude API subscriptions.
//        Maybe the only difference is the default prompt
//        Maybe the user has a custom AI Endpoint! Local or Remote! A domain I don't know of, and all the necessary ingredients

/// <summary>
/// A Definition of an Agent, all the information required to correctly construct it.
/// </summary>
internal interface IAgentDefinition
{

    public Guid Id { get; }

    public string Vendor { get; }

    public string DisplayName { get; }

    public string? DefaultModelId { get; }

    public IReadOnlyList<ModelDefinition> ModelDefinitions { get; }

    // NOTE : By returning an agent and requiring a token, the preservation of permissions is kept.
    public Agent? GetAgent(PlugIns.PlugInToken token, string model, string prompt);
    
    public Agent? GetAgent(PlugIns.PlugInToken token, string prompt);

    public IModel? GetModel(string model);

}

internal sealed class CliAgentDefinition(string vendor, string displayName) : IAgentDefinition
{

    public Guid Id { get; init; } = Guid.NewGuid();

    private List<string> WinSearchPaths { get; } = [];
    private List<string> MacSearchPaths { get; } = [];

    public string Vendor { get; } = vendor;

    public string DisplayName { get; } = displayName;

    public Platform Platforms { get; } = Platform.All;

    public string? DefaultModelId { get; }

    private List<ModelDefinition> PrivateModelDefinitions { get; } = [];
    public IReadOnlyList<ModelDefinition> ModelDefinitions => PrivateModelDefinitions;

    public Agent? GetAgent(PlugIns.PlugInToken token, string prompt)
        => GetAgent(token, DefaultModelId ?? "default", prompt);

    public IModel? GetModel(string modelName)
    {
        modelName ??= DefaultModelId!;
        ModelDefinition? def = PrivateModelDefinitions.FirstOrDefault(pd => string.Equals(pd.Id, modelName, StringComparison.OrdinalIgnoreCase));
        if (def is null) return null;

        IEnumerable<string> searchPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WinSearchPaths : MacSearchPaths;

        IModel? model = Vendor.ToLowerInvariant() switch
        {
            "anthropic" => new ClaudeDesktopModel(def.Id, searchPaths),
            "openai" => new CodexDesktopModel(def.Id, searchPaths),
            // "google" => (n) => new GeminiDesktopModel(n),
            // "deepseek" => (n) => new DeepSeekDesktopModel(n),

            _ => null
        };
        
        return model;
    }
    
    public Agent? GetAgent(PlugIns.PlugInToken token, string modelName, string prompt)
    {
        IModel? model = GetModel(modelName);
        if (model is null) return null;

        return Agent.FromModel(token, model, prompt);
    }

}

internal sealed class HttpAgentDefinition(string vendor, string displayName) : IAgentDefinition
{

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Vendor { get; } = vendor;

    public string DisplayName { get; } = displayName;

    public string? DefaultModelId { get; }

    public string? Host { get; init; }

    public ProtocolKind Protocol { get; init; } = ProtocolKind.Default;

    public string? ModelsRoute { get; init; }

    public string? ApiKeyEnvVar { get; init; }

    public string? ApiKey => Environment.GetEnvironmentVariable(ApiKeyEnvVar ?? string.Empty);

    private List<ModelDefinition> PrivateDefinitions { get; } = [];
    public IReadOnlyList<ModelDefinition> ModelDefinitions => PrivateDefinitions;

    public Agent? GetAgent(PlugIns.PlugInToken token, string prompt)
        => GetAgent(token, DefaultModelId ?? "default", prompt);

    public IModel? GetModel(string modelName)
    {
        modelName ??= DefaultModelId!;
        ModelDefinition? def = PrivateDefinitions.FirstOrDefault(pd => string.Equals(pd.Id, modelName, StringComparison.OrdinalIgnoreCase));
        if (def is null) return null;

        Func<string, IModel>? modelFactory = Vendor.ToLowerInvariant() switch
        {
            "anthropic" => Protocols(ClaudeModel.Default, ClaudeModel.Messages, ClaudeModel.Completions),
            "openai" => Protocols(ChatGptModel.Default, null, ChatGptModel.Completions, ChatGptModel.Responses),
            "google" => Protocols(GeminiModel.Default, null, GeminiModel.Completions, null),
            "deepseek" => Protocols(DeepSeekModel.Default, DeepSeekModel.Anthropic, DeepSeekModel.Completions, DeepSeekModel.Responses),
            "LM Studio" => Protocols(LocalModel.Default, LocalModel.Anthropic, LocalModel.Completions, LocalModel.Responses),
            // TODO : Fix
            // "Ollama" => Protocols(null),
            "Apple" => Protocols(LocalModel.Default, null, LocalModel.Completions, null),

            _ => Protocols(null, UnknownModel.Anthropic, UnknownModel.Completions, UnknownModel.Completions),
        };

        if (modelFactory is null) return null;

        IModel model = modelFactory.Invoke(def.Id);
        return model;
    }

    public Agent? GetAgent(PlugIns.PlugInToken token, string modelName, string prompt)
    {
        IModel? model = GetModel(modelName);
        if (model is null) return null;

        return Agent.FromModel(token, model, prompt);
    }

    private Func<string, IModel>? Protocols(Func<string, IModel> @default,
                                            Func<string, IModel>? messages = null,
                                            Func<string, IModel>? completions = null,
                                            Func<string, IModel>? responses = null)
        => Protocol switch
        {
            ProtocolKind.Default => @default,
            ProtocolKind.Messages => messages,
            ProtocolKind.Completions => completions,
            ProtocolKind.Responses => responses,

            _ => null
        };

    private Func<string, IModel>? Protocols(Func<string, Uri, IModel> @default,
                                            Func<string, Uri, IModel>? messages = null,
                                            Func<string, Uri, IModel>? completions = null,
                                            Func<string, Uri, IModel>? responses = null)
        => Protocol switch
        {
            _ when Host is null => null,

            ProtocolKind.Default when @default is not null => (m) => @default(m, new Uri(Host)),
            ProtocolKind.Messages when messages is not null => (m) => messages(m, new Uri(Host)),
            ProtocolKind.Completions when completions is not null => (m) => completions(m, new Uri(Host)),
            ProtocolKind.Responses when responses is not null => (m) => responses(m, new Uri(Host)),

            _ => null
        };

    private Func<string, IModel>? Protocols(Func<string, string, Uri, IModel>? @default,
                                            Func<string, string, Uri, IModel>? messages = null,
                                            Func<string, string, Uri, IModel>? completions = null,
                                            Func<string, string, Uri, IModel>? responses = null)
        => Protocol switch
        {
            _ when Host is null => null,

            ProtocolKind.Default when @default is not null => (m) => @default(m, Vendor, new Uri(Host)),
            ProtocolKind.Messages when messages is not null => (m) => messages(m, Vendor, new Uri(Host)),
            ProtocolKind.Completions when completions is not null => (m) => completions(m, Vendor, new Uri(Host)),
            ProtocolKind.Responses when responses is not null => (m) => responses(m, Vendor, new Uri(Host)),

            _ => null
        };

}

[Flags]
internal enum Platform { None = 0, Windows = 1, OSX = 2, All = Windows | OSX }

internal sealed record ModelDefinition(string DisplayName, string Id);
