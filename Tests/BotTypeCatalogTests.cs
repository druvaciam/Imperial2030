using System;
using System.IO;
using System.Linq;
using Imperial2030.Server.Services;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Bot-type discovery scans the deployment directory for exported ONNX models. It used to do that on
    /// every call — including from the [AllowAnonymous] `available-bots` endpoint, so any anonymous caller
    /// could drive unbounded directory scans, and from every AddBot.
    /// </summary>
    public class BotTypeCatalogTests : IDisposable
    {
        private readonly string _modelDirectory;

        public BotTypeCatalogTests()
        {
            _modelDirectory = Path.Combine(Path.GetTempPath(), "imperial-bot-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_modelDirectory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_modelDirectory, recursive: true); } catch { /* best effort */ }
        }

        private void WriteModel(string fileName) => File.WriteAllText(Path.Combine(_modelDirectory, fileName), "");

        [Fact]
        public void Available_AlwaysOffersTheBuiltInStrategies()
        {
            var catalog = new BotTypeCatalog(modelDirectory: _modelDirectory);

            Assert.Equal(new[] { "Default", "Aggressive", "Friendly", "Greedy", "Random" },
                         catalog.Available.Take(5).ToArray());
        }

        [Theory]
        [InlineData("RL.onnx")]
        [InlineData("imperial_ppo_bot.onnx")]
        public void Available_OffersRL_WhenEitherDefaultModelIsPresent(string fileName)
        {
            WriteModel(fileName);

            var catalog = new BotTypeCatalog(modelDirectory: _modelDirectory);

            Assert.Contains("RL", catalog.Available);
        }

        [Fact]
        public void Available_ListsAdditionalRLModels_AndIgnoresUnrelatedOnnxFiles()
        {
            WriteModel("RL.onnx");
            WriteModel("RL-3.onnx");
            WriteModel("something-else.onnx");

            var catalog = new BotTypeCatalog(modelDirectory: _modelDirectory);

            Assert.Contains("RL", catalog.Available);
            Assert.Contains("RL-3", catalog.Available);
            Assert.DoesNotContain("something-else", catalog.Available);
            // "RL" itself must not be listed twice - once from the default-model check, once from the scan.
            Assert.Equal(1, catalog.Available.Count(t => t == "RL"));
        }

        [Fact]
        public void Available_ScansTheDirectoryOnlyOnce()
        {
            WriteModel("RL-7.onnx");

            var catalog = new BotTypeCatalog(modelDirectory: _modelDirectory);
            Assert.Contains("RL-7", catalog.Available);

            // Delete the model out from under the catalog. A cached catalog still reports it; one that
            // re-scans per call would not - which is the whole point.
            File.Delete(Path.Combine(_modelDirectory, "RL-7.onnx"));

            Assert.Contains("RL-7", catalog.Available);
        }

        [Fact]
        public void Available_WithAMissingDirectory_StillOffersTheBuiltInStrategies()
        {
            var catalog = new BotTypeCatalog(modelDirectory: Path.Combine(_modelDirectory, "does-not-exist"));

            Assert.Contains("Default", catalog.Available);
            Assert.DoesNotContain("RL", catalog.Available);
        }
    }
}
