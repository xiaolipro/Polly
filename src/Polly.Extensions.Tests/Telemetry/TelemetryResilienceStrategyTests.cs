using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Polly.Extensions.Telemetry;
using Polly.Strategy;

namespace Polly.Extensions.Tests.Telemetry;

#pragma warning disable S103 // Lines should not be too long

[Collection("NonParallelizableTests")]
public class TelemetryResilienceStrategyTests : IDisposable
{
    private readonly FakeLogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDisposable _metering;
    private readonly List<MeteringEvent> _events = new();
    private Action<EnrichmentContext> _enricher = _ => { };

    public TelemetryResilienceStrategyTests()
    {
        _loggerFactory = TestUtilities.CreateLoggerFactory(out _logger);
        _metering = TestUtilities.EnablePollyMetering(_events);
    }

    [Fact]
    public void Ctor_Ok()
    {
        var duration = CreateStrategy().ExecutionDuration;

        duration.Unit.Should().Be("ms");
        duration.Description.Should().Be("The execution duration and execution result of resilience strategies.");
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void Execute_EnsureLogged(bool healthy)
    {
        var healthString = healthy ? "Healthy" : "Unhealthy";
        var strategy = CreateStrategy();

        strategy.Execute(
            (c, _) =>
            {
                if (!healthy)
                {
                    ((List<ReportedResilienceEvent>)c.ResilienceEvents).Add(new ReportedResilienceEvent("dummy"));
                }
            },
            ResilienceContext.Get(), string.Empty);

        var messages = _logger.GetRecords(new EventId(1, "StrategyExecuting")).ToList();
        messages.Should().HaveCount(1);
        messages[0].Message.Should().Be("Resilience strategy executing. Builder Name: 'my-builder', Strategy Key: 'my-key', Result Type: 'void'");
        messages = _logger.GetRecords(new EventId(2, "StrategyExecuted")).ToList();
        messages.Should().HaveCount(1);
        messages[0].Message.Should().Match($"Resilience strategy executed. Builder Name: 'my-builder', Strategy Key: 'my-key', Result Type: 'void', Result: 'void', Execution Health: '{healthString}', Execution Time: *ms");
    }

    [Fact]
    public void Execute_WithException_EnsureLogged()
    {
        var strategy = CreateStrategy();
        strategy.Invoking(s => s.Execute(_ => throw new InvalidOperationException("Dummy message."))).Should().Throw<InvalidOperationException>();

        var messages = _logger.GetRecords(new EventId(1, "StrategyExecuting")).ToList();
        messages.Should().HaveCount(1);
        messages[0].Message.Should().Be("Resilience strategy executing. Builder Name: 'my-builder', Strategy Key: 'my-key', Result Type: 'void'");

        messages = _logger.GetRecords(new EventId(2, "StrategyExecuted")).ToList();
        messages.Should().HaveCount(1);
        messages[0].Message.Should().Match($"Resilience strategy executed. Builder Name: 'my-builder', Strategy Key: 'my-key', Result Type: 'void', Result: 'Dummy message.', Execution Health: 'Healthy', Execution Time: *ms");
        messages[0].Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Execute_WithException_EnsureMetered()
    {
        var strategy = CreateStrategy();
        strategy.Invoking(s => s.Execute(_ => throw new InvalidOperationException("Dummy message."))).Should().Throw<InvalidOperationException>();

        var ev = _events.Single(v => v.Name == "strategy-execution-duration").Tags;

        ev.Count.Should().Be(5);
        ev["strategy-key"].Should().Be("my-key");
        ev["builder-name"].Should().Be("my-builder");
        ev["result-type"].Should().Be("void");
        ev["exception-name"].Should().Be("System.InvalidOperationException");
        ev["execution-health"].Should().Be("Healthy");
    }

    [Fact]
    public void Execute_Enrichers_Ok()
    {
        _enricher = context =>
        {
            context.Tags.Add(new KeyValuePair<string, object?>("my-custom-tag", "my-tag-value"));
        };
        var strategy = CreateStrategy();
        strategy.Execute(_ => true);

        var ev = _events.Single(v => v.Name == "strategy-execution-duration").Tags;

        ev.Count.Should().Be(6);
        ev["my-custom-tag"].Should().Be("my-tag-value");
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void Execute_WithResult_EnsureMetered(bool healthy)
    {
        var strategy = CreateStrategy();
        strategy.Execute(
            (c, _) =>
            {
                if (!healthy)
                {
                    ((List<ReportedResilienceEvent>)c.ResilienceEvents).Add(new ReportedResilienceEvent("dummy"));
                }

                return true;
            },
            ResilienceContext.Get(), string.Empty);

        var ev = _events.Single(v => v.Name == "strategy-execution-duration").Tags;

        ev.Count.Should().Be(5);
        ev["strategy-key"].Should().Be("my-key");
        ev["builder-name"].Should().Be("my-builder");
        ev["result-type"].Should().Be("Boolean");
        ev["exception-name"].Should().BeNull();

        if (healthy)
        {
            ev["execution-health"].Should().Be("Healthy");
        }
        else
        {
            ev["execution-health"].Should().Be("Unhealthy");
        }
    }

    private TelemetryResilienceStrategy CreateStrategy() => new("my-builder", "my-key", _loggerFactory, new List<Action<EnrichmentContext>> { c => _enricher?.Invoke(c) });
    public void Dispose()
    {
        _metering.Dispose();
        _loggerFactory.Dispose();
    }
}
