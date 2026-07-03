namespace Novelist.Core.App;

public sealed class InvalidContentPathException : Exception
{
    public InvalidContentPathException(string path, string reason)
        : base("The content path must stay inside the novelist workspace.")
    {
        Details = new Dictionary<string, string>
        {
            ["path"] = reason,
            ["value"] = path
        };
    }

    public IReadOnlyDictionary<string, string> Details { get; }
}
