
using System;

namespace Rhino.AI;

// TODO : Deserialize from somewhere
// TODO : Make not static

/// <summary>
/// Global User Preferences for AI Models, Agents
/// </summary>
public static class UserPreferences
{
    
    public static Guid AgentId { get; }

    public static PermissionSet Permissions { get; } = new();

}
