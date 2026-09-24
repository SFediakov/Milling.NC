namespace Miller.Machine.Links;

// A port or connection that cannot be opened, or one that failed while in use.
public sealed class MachineLinkException : IOException
{
    public MachineLinkException(string message)
        : base(message)
    {
    }

    public MachineLinkException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
