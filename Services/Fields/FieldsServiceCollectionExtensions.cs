namespace ByteBench.Services.Fields;

public static class FieldsServiceCollectionExtensions
{
    /// <summary>Custom fields, components and versions, issue templates and estimates, plus the Workbench (time &amp; cost) link.</summary>
    public static IServiceCollection AddIssueFields(this IServiceCollection services)
    {
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ComponentVersionService>();
        services.AddScoped<IssueTemplateService>();
        services.AddScoped<EstimateService>();
        services.AddScoped<Workbench.WorkbenchService>();
        Workbench.WorkbenchServiceCollectionExtensions.AddWorkbenchIntegration(services);
        // GQL: component, fixVersion, affectsVersion, estimates, wbs and cf[key].
        services.AddSingleton<Query.IQueryFieldProvider, IssueFieldQueryFields>();
        return services;
    }
}
