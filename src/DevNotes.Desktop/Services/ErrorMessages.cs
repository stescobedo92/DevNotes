using System.Globalization;
using DevNotes.Application.Abstractions;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;

namespace DevNotes.Desktop.Services;

/// <summary>Turns exceptions from file and index operations into localized, actionable messages.</summary>
public static class ErrorMessages
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            NoteAlreadyExistsException => Strings.Error_NoteExists,
            NoteNotFoundException or TrashEntryNotFoundException => Strings.Error_NoteNotFound,
            VaultFolderNotFoundException missing => Format(Strings.Error_FolderNotFound, missing.FolderPath),
            DirectoryNotFoundException => Format(Strings.Error_FolderNotFound, exception.Message),
            UnauthorizedAccessException => Format(Strings.Error_AccessDenied, exception.Message),
            ArgumentException => Strings.Error_InvalidFolder,
            IOException or InvalidDataException => Format(Strings.Error_Io, exception.Message),
            _ => Format(Strings.Error_Unexpected, exception.Message),
        };
    }

    /// <summary>
    /// Exceptions that represent an expected failure of an operation on notes (I/O, permissions,
    /// invalid user input). Anything else is a bug and must not be hidden behind a friendly message.
    /// </summary>
    public static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException;

    public static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);
}
