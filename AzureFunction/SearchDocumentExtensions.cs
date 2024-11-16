using Azure.Search.Documents.Models;

public static class SearchDocumentExtensions
{
    /// <summary>
    /// Safely gets a string value from a SearchDocument, returning an empty string if the key doesn't exist or the value is null
    /// </summary>
    public static string SafeGetString(this SearchDocument document, string key)
    {
        if (document == null || !document.ContainsKey(key))
        {
            return string.Empty;
        }

        return document[key]?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Safely gets a value of type T from a SearchDocument, returning the default value if the key doesn't exist or the value cannot be converted
    /// </summary>
    public static T SafeGetValue<T>(this SearchDocument document, string key, T defaultValue)
    {
        if (document == null || !document.ContainsKey(key))
        {
            return defaultValue;
        }

        try
        {
            var value = document[key];
            if (value == null)
            {
                return defaultValue;
            }

            // Handle boolean conversion specially since it's commonly used
            if (typeof(T) == typeof(bool) && value is string stringValue)
            {
                if (bool.TryParse(stringValue, out bool boolResult))
                {
                    return (T)(object)boolResult;
                }
                return defaultValue;
            }

            // Try to convert the value to the target type
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}