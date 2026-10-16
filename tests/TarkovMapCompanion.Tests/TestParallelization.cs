using Xunit;

// The active UI language is process-wide static state on Localizer, and most tests assume the
// English default. A test that switches it (see ExitLocalizationTests.TheEnglishEchoKeepsAnEnglish
// WrapperUnderChinese) would race every other test class, because xUnit parallelises across
// classes by default. Serialising the assembly is cheap here -- the suite runs in seconds -- and
// makes the shared state honest instead of accidentally ordered.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
