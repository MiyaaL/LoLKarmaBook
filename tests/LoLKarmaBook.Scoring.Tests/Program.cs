using LoLKarmaBook.Scoring.Tests;

var runner = new TestRunner();
PerformanceTests.Register(runner);
RatingTests.Register(runner);
EstimatedEloTests.Register(runner);
ScenarioTests.Register(runner);
return runner.Run();
