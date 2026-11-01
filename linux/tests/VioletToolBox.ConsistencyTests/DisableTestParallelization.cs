using Xunit;

// Consistency tests share static interception seams; run classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VioletToolBox.ConsistencyTests;
