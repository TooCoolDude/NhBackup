using NhBackup.WebApplication.Db.Entities;

namespace NhentaiBackup.WebApplication;

internal static class SyncronizerHelpers
{
    /// <summary>
    /// Normalizes a string by padding the numeric part before the first dot with leading zeros.
    /// </summary>
    /// <param name="input">The input string to normalize.</param>
    /// <param name="totalLength">The total length of the numeric part after padding.</param>
    /// <returns>The normalized string.</returns>
    public static string NormalizeBeforeDot(string input, int totalLength)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        int dotIndex = input.IndexOf('.');

        string numberPart = dotIndex >= 0
            ? input.Substring(0, dotIndex)
            : input;

        if (!int.TryParse(numberPart, out var number))
            return input;

        string normalized = number.ToString($"D{totalLength}");

        return dotIndex >= 0
            ? normalized + input.Substring(dotIndex)
            : normalized;
    }

    public static string GetJapaneseTitle(object jpTitle)
    {
        if (jpTitle == null) return null;

        var prop = jpTitle.GetType().GetProperty("String");
        if (prop != null)
        {
            var value = prop.GetValue(jpTitle);
            return value as string;
        }

        return null;
    }
    public static string ToFullPath(string datafolder,string relativePath)
    {
        return Path.Combine(
            datafolder,
            relativePath.TrimStart('/'));
    }
    public static bool ValidateDbGalleryEntity(Gallery? existing)
    {
        if (existing.MediaPaths == null)
        {
            return false;
        }
        if (existing.NumPages != existing.MediaPaths.Count)
        {
            return false;
        }
        return true;
    }
}