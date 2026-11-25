// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

namespace SecretModel.Core.Text;

/// <summary>
/// Text rules shared by the tokenizer and quantizer, written to match the Python reference
/// (reference/python), which lowercases with <c>str.lower()</c>.
/// </summary>
internal static class ReferenceText
{
    /// <summary>Lowercases text the way the Python reference does.</summary>
    public static string Lower(string text) => text.ToLowerInvariant();
}
