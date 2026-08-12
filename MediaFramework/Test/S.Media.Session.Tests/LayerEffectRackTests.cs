using S.Media.Compositor.Effects;
using S.Media.Core.Buses;
using S.Media.Core.Video.Effects;
using Xunit;

namespace S.Media.Session.Tests;

public sealed class LayerEffectRackTests
{
    [Fact]
    public void RackBuildsInAuthoredOrderAndSkipsBypassedInstances()
    {
        var placement = new VideoPlacementSpec(
            "screen",
            0,
            Effects:
            [
                Effect("colour", BrightnessContrastVideoEffect.EffectId,
                    new("brightness", .1), new("contrast", 1.2)),
                Effect("off", ChromaKeyVideoEffect.EffectId, enabled: false),
                Effect("key", ChromaKeyVideoEffect.EffectId,
                    new("similarity", .6), new("smoothness", .2)),
            ]);

        var effects = ClipCompositionRuntime.LayerSlot.BuildLayerEffects(placement)!;

        Assert.Collection(
            effects,
            effect => Assert.Equal(BrightnessContrastVideoEffect.EffectId, effect.Descriptor.Id),
            effect => Assert.Equal(ChromaKeyVideoEffect.EffectId, effect.Descriptor.Id));
        Assert.Equal(.6f, effects[1].Values[3], 5);
    }

    [Fact]
    public void RegistryResolvesPluginTypeAndReceivesScalarOverlay()
    {
        string? received = null;
        var descriptor = new VideoLayerEffectDescriptor(
            "test.tint.v1",
            "return src;",
            [new VideoLayerEffectParameter("amount", 1)]);
        var registry = BusRegistryBuilder.Build(builder => builder.AddLayerEffect(
            descriptor.Id,
            json =>
            {
                received = json;
                return new VideoLayerEffect(descriptor, [.5f]);
            }));
        var placement = new VideoPlacementSpec(
            "screen",
            0,
            Effects: [Effect("plugin", descriptor.Id, new("amount", .5), configJson: "{\"mode\":\"warm\"}")]);

        var effect = Assert.Single(ClipCompositionRuntime.LayerSlot.BuildLayerEffects(placement, registry)!);

        Assert.Same(descriptor, effect.Descriptor);
        Assert.Contains("\"mode\":\"warm\"", received);
        Assert.Contains("\"amount\":0.5", received);
    }

    [Fact]
    public void ArbitraryParameterAutomationTargetsStableInstance()
    {
        var automation = new ClipCompositionRuntime.PlacementEffectAutomation();
        automation.Set("key", "keyR", .75);
        var placement = new VideoPlacementSpec(
            "screen",
            0,
            Effects: [Effect("key", ChromaKeyVideoEffect.EffectId, new("keyR", .1))]);

        var applied = automation.Apply(placement);

        Assert.Equal(.75, Assert.Single(applied.Effects!).Parameters.Single().Value, 6);
    }

    [Fact]
    public async Task AudioInsertAutomationAndControllerOwnershipReachTheLiveEffect()
    {
        var created = new List<RecordingAudioEffect>();
        string? receivedConfig = null;
        var registry = BusRegistryBuilder.Build(builder => builder.AddAudioEffect(
            RecordingAudioEffect.TypeId,
            config =>
            {
                receivedConfig = config;
                var effect = new RecordingAudioEffect();
                created.Add(effect);
                return effect;
            }));
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(chunks: 100_000),
            new RecordingAudioBackend(),
            effectRegistry: registry);
        var document = new ShowDocument(
            ShowDocumentValidator.CurrentVersion,
            [new CueDefinition("cue", 1, "Cue")],
            [
                new ShowClipBinding("cue", "fake://audio")
                {
                    AudioRoutes = [new ShowClipAudioRoute()],
                    AudioEffects =
                    [
                        new ShowAudioEffectInstance(
                            "insert",
                            RecordingAudioEffect.TypeId,
                            true,
                            [new ShowEffectParameterValue(RecordingAudioEffect.AmountParameterId, -3)]),
                    ],
                    AudioEffectEnvelopes =
                    [
                        new ShowAudioEffectEnvelope(
                            "insert",
                            RecordingAudioEffect.AmountParameterId,
                            [
                                new ShowEnvelopePoint(TimeSpan.Zero, -6),
                                new ShowEnvelopePoint(TimeSpan.FromSeconds(10), -6),
                            ]),
                    ],
                },
            ],
            [],
            []);

        await session.LoadDocumentAsync(document);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue"));

        var effect = Assert.Single(created);
        Assert.True(effect.IsConfigured);
        Assert.Contains("\"amount\":-3", receivedConfig);
        Assert.Equal(-6, effect.LastValue);

        var instance = Assert.IsType<ShowCueInstance>(await session.CaptureActiveCueInstanceAsync("cue"));
        var owner = Guid.NewGuid();
        Assert.True(await session.ApplyControllerAudioEffectAsync(
            instance, owner, "insert", RecordingAudioEffect.AmountParameterId, 4, claim: true));
        Assert.Equal(4, effect.LastValue);

        Assert.True(await session.RebuildActiveClipAudioOutputsAsync("cue", [new ShowClipAudioRoute()]));
        var rebuiltEffect = Assert.IsType<RecordingAudioEffect>(created[^1]);
        Assert.NotSame(effect, rebuiltEffect);
        Assert.Equal(4, rebuiltEffect.LastValue);

        // The cue lane keeps advancing beneath the claimed controller value. Clearing the controller
        // restores that latest cue-owned value instead of the insert's static authored default.
        await Task.Delay(TimeSpan.FromMilliseconds(40));
        Assert.Equal(4, rebuiltEffect.LastValue);
        Assert.True(await session.ClearControllerAudioEffectAsync(
            instance, owner, "insert", RecordingAudioEffect.AmountParameterId));
        Assert.Equal(-6, rebuiltEffect.LastValue);
    }

    [Fact]
    public void ShowDocumentThreeRoundTripsGenericVideoAndAudioEffects()
    {
        var document = new ShowDocument(
            ShowDocumentValidator.CurrentVersion,
            [new CueDefinition("cue", 1, "Cue")],
            [
                new ShowClipBinding("cue", "fake://media", CompositionId: "screen")
                {
                    Placement = new ShowVideoPlacement
                    {
                        Effects =
                        [
                            new ShowLayerEffectInstance(
                                "video-fx", "plugin.video", true,
                                [new ShowEffectParameterValue("amount", .4)], "{\"mode\":\"warm\"}"),
                        ],
                    },
                    AudioEffects =
                    [
                        new ShowAudioEffectInstance(
                            "audio-fx", "plugin.audio", true,
                            [new ShowEffectParameterValue("gain", -3)]),
                    ],
                    AudioEffectEnvelopes =
                    [
                        new ShowAudioEffectEnvelope(
                            "audio-fx", "gain", [new ShowEnvelopePoint(TimeSpan.Zero, -3)]),
                    ],
                },
            ],
            [new ShowComposition("screen", "Screen", 1920, 1080)],
            []);

        var restored = ShowDocument.FromJson(document.ToJson());

        Assert.Equal(ShowDocumentValidator.CurrentVersion, restored.Version);
        var clip = Assert.Single(restored.Clips);
        var video = Assert.Single(clip.Placement!.Effects!);
        Assert.Equal("video-fx", video.InstanceId);
        Assert.Equal(.4, Assert.Single(video.Parameters).Value);
        var audio = Assert.Single(clip.AudioEffects!);
        Assert.Equal("audio-fx", audio.InstanceId);
        Assert.Equal(-3, Assert.Single(audio.Parameters).Value);
        Assert.Equal("gain", Assert.Single(clip.AudioEffectEnvelopes!).ParameterId);
    }

    private static ShowLayerEffectInstance Effect(
        string id,
        string type,
        ShowEffectParameterValue? first = null,
        ShowEffectParameterValue? second = null,
        bool enabled = true,
        string? configJson = null) =>
        new(
            id,
            type,
            enabled,
            new[] { first, second }.Where(value => value is not null).Cast<ShowEffectParameterValue>().ToArray(),
            configJson);

    private sealed class RecordingAudioEffect : IAutomatableAudioBusEffect
    {
        public const string TypeId = "test.audio.v1";
        public const string AmountParameterId = "amount";

        private float _lastValue = float.NaN;

        public IReadOnlyList<S.Media.Core.Effects.EffectParameterDescriptor> Parameters { get; } =
        [
            new(AmountParameterId, "Amount", -12, 12, 0),
        ];

        public bool IsConfigured { get; private set; }
        public float LastValue => Volatile.Read(ref _lastValue);

        public void Configure(AudioFormat format) => IsConfigured = true;

        public bool TrySetParameter(string parameterId, float value, TimeSpan smoothing)
        {
            if (parameterId != AmountParameterId)
                return false;
            Volatile.Write(ref _lastValue, value);
            return true;
        }

        public void Process(Span<float> interleaved, long samplePosition) { }

        public void Dispose() { }
    }
}
