using SFW.Services;
using Xunit;

namespace SFW.Tests;

/// <summary>
/// Settings writes must be atomic: a canceled or failed write leaves the previous
/// file intact and never leaves temporary files behind.
/// </summary>
public sealed class AtomicFileTests
{
    [Fact]
    public async Task CanceledWritePreservesPreviousFileAndCleansUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sfw-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            AtomicFile.WriteAllText(path, "old");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => AtomicFile.WriteAllTextAsync(path, "new", stop.Token));
            Assert.True(File.ReadAllText(path) == "old", "Canceled writes must preserve the previous file.");

            await AtomicFile.WriteAllTextAsync(path, "配置", TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllText(path) == "配置", "Successful writes must replace the previous content.");
            Assert.True(Directory.GetFiles(directory).Length == 1, "Temporary files must be cleaned up.");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
