using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

public enum ImportStatus { Queued, Cloning, Done, Failed, Cancelled }

/// <summary>
/// A repository import (the import wizard): a background job that clones all branches and tags of a remote repository into a new
/// ByteBench repository. Never holds credentials (they live in memory for the one run); see
/// <see cref="ByteBench.Services.Importing.ImportService"/>.
/// </summary>
public class RepositoryImport
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int? RequestedById { get; set; }
    public User? RequestedBy { get; set; }

    /// <summary>The source URL, without credentials.</summary>
    [Required, MaxLength(2048)] public string SourceUrl { get; set; } = "";
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(100)] public string Slug { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>The branch HEAD will point at (chosen in the wizard; the source's default branch unless changed).</summary>
    [MaxLength(250)] public string? DefaultBranch { get; set; }
    /// <summary>After the import, keep the repository as a pull mirror of the source (reusing the entered credentials, encrypted).</summary>
    public bool KeepAsMirror { get; set; }

    public ImportStatus Status { get; set; } = ImportStatus.Queued;
    [MaxLength(2000)] public string? Error { get; set; }
    /// <summary>Bytes on disk so far (updated while cloning).</summary>
    public long BytesReceived { get; set; }
    public int BranchCount { get; set; }
    public int TagCount { get; set; }
    /// <summary>The created repository, once the import is done (null if it was later deleted).</summary>
    public int? RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public bool IsActive => Status is ImportStatus.Queued or ImportStatus.Cloning;
}
