using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Core.Retention;
using RecordAuditTrails.Services.Auditing;
using RecordAuditTrails.Services.Documents;
using RecordAuditTrails.Services.Records;
using RecordAuditTrails.Services.Retention;

namespace RecordAuditTrails.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuditServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RetentionPolicy(sp.GetRequiredService<IOptions<RetentionOptions>>().Value));
        services.AddSingleton(ReportExporter.CreateDefault());
        services.AddScoped<IAuditTrailWriter, AuditTrailWriter>();
        services.AddScoped<IRecordService, RecordService>();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<IRecordExportService, RecordExportService>();
        services.AddScoped<IRetentionService, RetentionService>();
        services.AddScoped<ISystemDocumentService, SystemDocumentService>();
        return services;
    }
}
