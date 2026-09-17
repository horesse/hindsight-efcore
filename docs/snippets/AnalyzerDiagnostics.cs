using InsuranceSample;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

public static class AnalyzerDiagnostics
{
    public static async Task BulkCloseAsync(AppDbContext db)
    {
        #region hdst001-fires
        // Policy is IsTemporal() (see AppDbContext above), so this line raises HDST001: under
        // HistoryWriter.Interceptor (the default) it bypasses SaveChanges and writes no history.
        await db.Policies.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PolicyStatus.Cancelled));
        #endregion hdst001-fires
    }

    public static async Task BulkCloseUnderTriggerAsync(AppDbContext db)
    {
        #region hdst001-suppressed
        // This DbContext is configured with HistoryWriter.Trigger (see Choosing a history writer), so
        // the database trigger records this write regardless - HDST001 doesn't know that and still
        // fires. Verified at this call site, so it's suppressed rather than worked around.
#pragma warning disable HDST001 // verified: this DbContext uses HistoryWriter.Trigger
        await db.Policies.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PolicyStatus.Cancelled));
#pragma warning restore HDST001
        #endregion hdst001-suppressed
    }
}
