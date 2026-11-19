using GitBench.Components.Repositories;
using GitBench.Data;
using MudBlazor;

namespace GitBench.Components.Pipelines;

/// <summary>Small helpers shared by the environment and deployment views.</summary>
public static class DeploymentUi
{
    /// <summary>Asks for an approval or rejection comment. Null if canceled; "" for no comment.</summary>
    public static async Task<string?> PromptCommentAsync(IDialogService dialogs, string title, string message, string button, bool required)
    {
        var parameters = new DialogParameters<EditorPromptDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.Label, required ? "Reason" : "Comment (optional)" },
            { x => x.ButtonText, button },
            { x => x.Multiline, true },
            { x => x.AllowEmpty, !required },
        };
        var dialog = await dialogs.ShowAsync<EditorPromptDialog>(title, parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: string text } ? text : null;
    }

    /// <summary>"waiting for approval", "running", "deployed", "failed", "canceled" for a deployment's status.</summary>
    public static string StatusLabel(Deployment d) => d.Status switch
    {
        PipelineStatus.Manual => "waiting for approval",
        PipelineStatus.Running => d.Action == DeploymentAction.Stop ? "stopping" : "deploying",
        PipelineStatus.Passed => d.Action == DeploymentAction.Stop ? "stopped" : "deployed",
        PipelineStatus.Failed or PipelineStatus.Error => "failed",
        PipelineStatus.Canceled => "canceled",
        _ => d.Status.ToString().ToLowerInvariant(),
    };
}
