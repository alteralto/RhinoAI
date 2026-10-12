using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using FuzzySharp;
using FuzzySharp.PreProcess;

using Rhino.AI.Models;
using System.Diagnostics.CodeAnalysis;

namespace Rhino.AI.Tools;

/// <summary>
/// An MCP tool used for delegating tasks to a SubAgent.
/// </summary>
public sealed record DelegateTool(PlugIns.PlugInToken token) : Tool("delegate",
                                "Used for delegating tasks with 0 context. This tool creates a sub agent with read-only permissions",
                                true,
                                false,
                                [
                                    // Agent
                                    new ("model", "The Model for the SubAgent", ToolArgType.String, true),

                                    // Context
                                    new ("prompt", "The default prompt for the agent", ToolArgType.String, true),

                                    // TODO : Skills + Tools to enable
                                ])
{

    private PlugIns.PlugInToken Token { get; } = token;

    public override async Task<ToolReturn> UseAsync(IReadOnlyList<IToolArg> args, CancellationToken token)
    {
        string context = string.Empty;

        if (!args.TryGetString(Args[0].Name, out string modelName))
            return ToolReturn.Failure("model parameter is mandatory", "Call delegate again with model set to the model the sub agent should run on.");

        if (!args.TryGetString(Args[1].Name, out string prompt))
            return ToolReturn.Failure("prompt parameter is mandatory", "Call delegate again with context set to everything the sub agent needs, since it starts with none.");

        if (!Instances.Agents.Definitions.Any(d => d.ModelDefinitions.Count > 0))
        {
            return ToolReturn.Failure("This session cannot create sub agents.", "Do the work yourself instead of delegating it.");
        }

        if (!TryGetModel(modelName, out Models.IModel? model))
        {
            IEnumerable<Models.IModel> availableModels = AvailableModels();

            IEnumerable<Models.IModel> likelyModels = availableModels
                .OrderByDescending(m => Fuzz.WeightedRatio(modelName, m.Name, PreprocessMode.Full))
                .Take(3);
            IEnumerable<string> likelyModelNames = likelyModels.Select(t => t.Name);
            string likelyModelString = string.Join(" or ", likelyModelNames);
            return ToolReturn.Failure($"{modelName} is not available", $"Did you mean {likelyModelString}?");
        }

        if (model is null || !model.Available)
        {
            return ToolReturn.Failure($"{modelName} is not available", $"Try one of {string.Join(", ", AvailableModels().Select(m => m.Name))}");
        }

        Agent? agent = Agent.FromModel(Token, model, prompt);
        if (agent is null)
        {
            // TODO : Improve this message. I don't know why it would ever happen
            return ToolReturn.Failure($"{model.Name} is not available", $"Try one of {string.Join(", ", AvailableModels().Select(m => m.Name))}");
        }

        IEnumerable<ITurn> turns = await agent.SendAsync(context, token).ConfigureAwait(false);

        StringBuilder report = new();
        foreach (ITurn turn in turns)
        {
            if (turn is MessageTurn message)
            {
                report.AppendLine(message.Message);
            }
        }

        return report.Length == 0
            ? ToolReturn.Failure("The sub agent finished without reporting anything.", "Delegate again with a context that states what the sub agent should report back.")
            : ToolReturn.Success(report.ToString().TrimEnd());
    }

    private class ModelCompraer : IEqualityComparer<IModel>
    {
        public bool Equals(IModel? x, IModel? y)
        {
            if (x is null && y is null) return true;
            if (x is null && y is not null) return false;
            if (y is null && x is not null) return false;
            
            return string.Equals(x!.Name, y!.Name, System.StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode([DisallowNull] IModel obj) => obj.Name.GetHashCode();
        
    }

    private static HashSet<IModel> AvailableModels()
    {
        HashSet<IModel> models = new (new ModelCompraer());
        foreach(IAgentDefinition agentDef in Instances.Agents.Definitions)
        {
            foreach(ModelDefinition modelDef in agentDef.ModelDefinitions)
            {
                IModel? model = agentDef.GetModel(modelDef.Id);
                if (model is null) continue;
                models.Add(model);
            }
        }

        return models;
    }

    // TODO : Surely this should also ensure that the definition is desktoppy or api'y or local-y etc.
    // BUG : A Desktop model delegating could end up calling an API model or VICE VERSA
    private static bool TryGetModel(string modelName, out IModel? model)
    {
        // Start with the Default
        model = Instances.Agents.PreferredDefinition?.GetModel(modelName);
        if (model is not null) return true;

        foreach(IAgentDefinition def in Instances.Agents.Definitions)
        {
            model = def.GetModel(modelName);
            if (model is null) continue;
            return true;
        }

        model = default!;
        return false;
    }

}
