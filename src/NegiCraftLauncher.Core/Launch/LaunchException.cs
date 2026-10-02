using System;

namespace NegiCraftLauncher.Core.Launch;

/// <summary>An error the user can act on, as opposed to an unexpected internal failure.</summary>
public sealed class LaunchException : Exception
{
    public LaunchException(string message) : base(message)
    {
    }

    public LaunchException(string message, Exception inner) : base(message, inner)
    {
    }
}
