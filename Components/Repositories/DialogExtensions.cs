using MudBlazor;

namespace GitBench.Components.Repositories;

/// <summary>Confirmation helpers used by the repository and pipeline pages (replace JS confirm()).</summary>
public static class DialogExtensions
{
    /// <summary>A yes/cancel message box. True only when the user picked <paramref name="yesText"/>.</summary>
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message, string yesText = "Delete")
    {
        var result = await dialogs.ShowMessageBoxAsync(
            new MessageBoxOptions { Title = title, Message = message, YesText = yesText, CancelText = "Cancel" },
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = true });
        return result == true;
    }

    /// <summary>Shows <see cref="TypeToConfirmDialog"/>. Returns the typed text when confirmed, otherwise null.</summary>
    public static async Task<string?> TypeToConfirmAsync(this IDialogService dialogs, string title, string message, string confirmText, string buttonText = "Delete")
    {
        var parameters = new DialogParameters<TypeToConfirmDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.ConfirmText, confirmText },
            { x => x.ConfirmButtonText, buttonText },
        };
        var dialog = await dialogs.ShowAsync<TypeToConfirmDialog>(title, parameters,
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: string typed } ? typed : null;
    }
}
