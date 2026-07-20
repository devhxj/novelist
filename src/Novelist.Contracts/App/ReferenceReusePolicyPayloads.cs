namespace Novelist.Contracts.App;

public static class ReferenceCorpusLicenseStates
{
    public const string Unknown = "unknown";
    public const string PublicDomain = "public_domain";
    public const string CreativeCommons = "cc";
    public const string Authorized = "authorized";
    public const string Restricted = "restricted";
    public const string Forbidden = "forbidden";

    public static IReadOnlyList<string> All { get; } =
    [
        Unknown,
        PublicDomain,
        CreativeCommons,
        Authorized,
        Restricted,
        Forbidden
    ];
}

public static class ReferenceCorpusReusePolicies
{
    public const string VerbatimOk = "verbatim_ok";
    public const string AdaptedOnly = "adapted_only";
    public const string ReferenceOnly = "reference_only";
    public const string Forbidden = "forbidden";

    public static IReadOnlyList<string> All { get; } =
    [
        VerbatimOk,
        AdaptedOnly,
        ReferenceOnly,
        Forbidden
    ];
}
