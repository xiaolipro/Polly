using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Moq;
using Polly.Strategy;
using Polly.Telemetry;

namespace Polly.TestUtils;

#pragma warning disable CA1031 // Do not catch general exception types

public static class TestUtilities
{
    public static Task AssertWithTimeoutAsync(Func<Task> assertion) => AssertWithTimeoutAsync(assertion, TimeSpan.FromSeconds(60));

    public static Task AssertWithTimeoutAsync(Action assertion) => AssertWithTimeoutAsync(
        () =>
        {
            assertion();
            return Task.CompletedTask;
        },
        TimeSpan.FromSeconds(60));

    public static async Task AssertWithTimeoutAsync(Func<Task> assertion, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await assertion().ConfigureAwait(false);
                return;
            }
            catch (Exception) when (watch.Elapsed < timeout)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }

    public static ResilienceStrategyTelemetry CreateResilienceTelemetry(DiagnosticSource source)
        => new(new ResilienceTelemetrySource("dummy-builder", new ResilienceProperties(), "strategy-name", "strategy-type"), source);

    public static ResilienceStrategyTelemetry CreateResilienceTelemetry(Action<IResilienceArguments> callback)
        => new(new ResilienceTelemetrySource("dummy-builder", new ResilienceProperties(), "strategy-name", "strategy-type"), new CallbackDiagnosticSource(callback));

    public static ILoggerFactory CreateLoggerFactory(out FakeLogger logger)
    {
        logger = new FakeLogger();
        var loggerFactory = new Mock<ILoggerFactory>(MockBehavior.Strict);
        loggerFactory.Setup(v => v.CreateLogger("Polly")).Returns(logger);
        loggerFactory.Setup(v => v.Dispose());

        return loggerFactory.Object;
    }

    public static IDisposable EnablePollyMetering(ICollection<MeteringEvent> events)
    {
        var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Polly")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
        meterListener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
        meterListener.Start();

        void OnMeasurementRecorded<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            => events.Add(new MeteringEvent(instrument.Name, tags.ToArray().ToDictionary(v => v.Key, v => v.Value)));

        return meterListener;
    }

#pragma warning disable S107 // Methods should not have too many parameters
    public static void ReportEvent(
        this DiagnosticSource source,
        string eventName,
        string builderName,
        ResilienceProperties builderProperties,
        string strategyName,
        string strategyType,
        IResilienceArguments arguments,
        Outcome? outcome)
#pragma warning restore S107 // Methods should not have too many parameters
    {
        source.Write(eventName, new TelemetryEventArguments(new ResilienceTelemetrySource(builderName, builderProperties, strategyName, strategyType), eventName, arguments, outcome));
    }

    public static ResilienceContext WithResultType<T>(this ResilienceContext context)
    {
        context.Initialize<T>(true);
        return context;
    }

    private sealed class CallbackDiagnosticSource : DiagnosticSource
    {
        private readonly Action<IResilienceArguments> _callback;

        public CallbackDiagnosticSource(Action<IResilienceArguments> callback) => _callback = callback;

        public override bool IsEnabled(string name) => true;

        public override void Write(string name, object? value) => _callback((value as TelemetryEventArguments)!.Arguments);
    }
}
