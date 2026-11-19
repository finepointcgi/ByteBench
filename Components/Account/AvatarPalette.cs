namespace ByteBench.Components.Account;

/// <summary>Avatar colors offered to users (new accounts get a random one).</summary>
internal static class AvatarPalette
{
    public static readonly string[] Colors =
    [
        "#6366f1", // indigo
        "#8b5cf6", // violet
        "#ec4899", // pink
        "#f43f5e", // rose
        "#f97316", // orange
        "#f59e0b", // amber
        "#10b981", // emerald
        "#14b8a6", // teal
        "#0ea5e9", // sky
        "#3b82f6", // blue
        "#64748b", // slate
        "#84cc16", // lime
    ];

    public static string Random() => Colors[System.Random.Shared.Next(Colors.Length)];
}
