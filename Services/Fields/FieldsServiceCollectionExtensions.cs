namespace GitBench.Services.Fields;

public static class FieldsServiceCollectionExtensions
{
    /// <summary>Custom fields, components and versions, issue templates and time tracking.</summary>
    public static IServiceCollection AddIssueFields(this IServiceCollection services)
    {
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ComponentVersionService>();
        services.AddScoped<IssueTemplateService>();
        services.AddScoped<TimeTrackingService>();
        return services;
    }
}
