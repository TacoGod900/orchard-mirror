namespace Orchard.Mirror.Tests;

using System.Text.Json;
using Orchard.Mirror.Agent.Windows;

/// <summary>
/// Orchard Mirror's test suite. Like every other suite in this repository it is a dependency-free
/// executable harness rather than an adapter for <c>dotnet test</c>: add each test to the table
/// below and make failures deterministic and self-describing.
///
/// <para>Tests that need hardware (an attached iPhone, a GPU, a recorded RTP capture) must skip
/// loudly through <see cref="SkipException"/> rather than silently passing, so this suite can run unattended
/// in <c>eng/check-mirror.ps1</c> without ever reporting a green run it did not earn.</para>
/// </summary>
internal static class Program
{
    private static readonly (string Name, Action Body)[] Tests =
    [
        ("keeps Orchard Mirror out of the Orchard product solution", KeepsMirrorOutOfProductSolution),
        ("lists every Mirror project in the Mirror solution", ListsMirrorProjectsInMirrorSolution),
        ("serializes the versioned CoreDevice agent request contract", AgentProtocolTests.SerializesRequest),
        ("parses a successful CoreDevice agent response", AgentProtocolTests.ParsesSuccess),
        ("parses stable CoreDevice agent errors", AgentProtocolTests.ParsesError),
        ("rejects an incompatible CoreDevice agent protocol version", AgentProtocolTests.RejectsWrongVersion),
        ("normalizes window edges to the full touchscreen range", AgentProtocolTests.NormalizesTouchCoordinates),
        ("distinguishes locked and unlocked CoreDevice states", AgentProtocolTests.DistinguishesLockStates),
        ("reads the live iOS display resolution", CoreDeviceDisplaySizeTests.ReadsTheLiveIosDisplayResolution),
        ("prefers the primary display over an external one", CoreDeviceDisplaySizeTests.PrefersThePrimaryDisplayOverAnExternalOne),
        ("reads the object form of a display size", CoreDeviceDisplaySizeTests.ReadsTheObjectSizeForm),
        ("reports no resolution rather than guessing", CoreDeviceDisplaySizeTests.ReportsNoResolutionRatherThanGuessing),
        ("crops away encoder padding but never real pixels", CoreDeviceDisplaySizeTests.OnlyCropsAwayEncoderPadding),
        ("receives a complete relayed RTP datagram over loopback", RtpLoopbackReceiverTests.ReceivesDatagram),

        ("says what went wrong after the mirror has worked once", StatusRouterTests.SaysWhatWentWrongAfterTheMirrorHasWorked),
        ("never discards what the caller said", StatusRouterTests.NeverDiscardsWhatTheCallerSaid),
        ("keeps routine chatter off the live picture", StatusRouterTests.KeepsRoutineChatterOffTheLivePicture),
        ("speaks up when there is no picture to protect", StatusRouterTests.SpeaksUpWhenThereIsNoPictureToProtect),
        ("stops with something to press", StatusRouterTests.StopsWithSomethingToPress),
        ("keeps something turning while it retries", StatusRouterTests.KeepsSomethingTurningWhileItRetries),
        ("colours a warning apart from progress and failure", StatusRouterTests.ColoursAWarningDifferentlyFromProgressAndFailure),
        ("never shows a blank headline", StatusRouterTests.NeverShowsABlankHeadline),
        ("gives every toast an expiry", StatusRouterTests.GivesEveryToastAnExpiry),
        ("hands the screen back after saying ready", StatusRouterTests.HandsTheScreenBackAfterSayingReady),
        ("lets a stopped state stay", StatusRouterTests.LetsAStoppedStateStay),
        ("shows an unrecognised state rather than swallowing it", StatusRouterTests.ShowsAnUnrecognisedStateRatherThanSwallowingIt),

        ("never covers the phone for a stall that recovers", StatusRouterTests.AStallOverAHeldFrameNeverCoversThePhone),
        ("takes the surface for a stall with nothing held", StatusRouterTests.AStallWithNothingHeldTakesTheSurface),
        ("suppresses routine news over a frozen picture", StatusRouterTests.RoutineNewsIsSuppressedOverAFrozenPicture),
        ("keeps reconnection progress off a held picture", StatusRouterTests.ReconnectionProgressStaysOffAHeldPicture),
        ("takes the surface for a failure over a held frame", StatusRouterTests.AFailureTakesTheSurfaceEvenOverAHeldFrame),
        ("agrees the held-frame grace with the retry ladder", StatusRouterTests.TheHeldFrameGraceAgreesWithTheRetryLadder),
        ("runs believability out rather than latching it", StatusRouterTests.BelievabilityRunsOutRatherThanLatching),
        ("shows a confirmation as a note, never a panel", StatusRouterTests.AConfirmationIsANoteOverAPictureAndNothingOverNone),

        ("keeps plain ASCII intact when pasting", ClipboardTextTests.PlainAsciiSurvivesUnchanged),
        ("folds smart punctuation to ASCII when pasting", ClipboardTextTests.SmartPunctuationBecomesAscii),
        ("decomposes accented letters when pasting", ClipboardTextTests.AccentsDecomposeToBaseLetters),
        ("drops and counts untypeable characters", ClipboardTextTests.UntypeableCharactersAreDroppedAndCounted),
        ("normalizes line endings without eating blank lines", ClipboardTextTests.LineEndingsNormalizeWithoutEatingBlankLines),
        ("folds odd whitespace to a plain space", ClipboardTextTests.OddWhitespaceBecomesPlainSpace),
        ("handles empty and null clipboard text", ClipboardTextTests.EmptyAndNullAreHandled),
        ("truncates and flags an overlong paste", ClipboardTextTests.OverlongTextIsTruncatedAndFlagged),
        ("leaves every pasted character typeable", ClipboardTextTests.EveryOutputCharacterIsTypeable),

        ("maps a 1:1 window selection unchanged", FrameSelectionTests.OneToOneWindowMapsUnchanged),
        ("scales a smaller window up to frame pixels", FrameSelectionTests.ASmallerWindowScalesUpToFramePixels),
        ("treats a backwards drag as the same rectangle", FrameSelectionTests.ABackwardsDragIsTheSameRectangle),
        ("clamps a drag off the edge to the frame", FrameSelectionTests.ADragOffTheEdgeClampsToTheFrame),
        ("keeps the far edges inside the frame", FrameSelectionTests.TheFarEdgesStayInsideTheFrame),
        ("rejects a bare click as a selection", FrameSelectionTests.ABareClickIsNotASelection),
        ("accepts a word-sized box as a selection", FrameSelectionTests.AWordSizedBoxIsASelection),
        ("refuses degenerate window or frame sizes", FrameSelectionTests.DegenerateSizesAreRefused),

        ("round-trips a key for every tier", LicensingTests.EveryTierRoundTrips),
        ("refuses garbage license keys", LicensingTests.GarbageIsRefused),
        ("fails the check on one wrong character", LicensingTests.OneWrongCharacterFailsTheCheck),
        ("forgives case and formatting in keys", LicensingTests.CaseAndFormattingAreForgiven),
        ("rejects ambiguous characters in keys", LicensingTests.KeyPayloadRejectsAmbiguousCharacters),
        ("accepts real keys and explains bad ones", LicensingTests.TheOfflineGatewayAcceptsRealKeysAndExplainsBadOnes),

        ("measures a toast's hold from full opacity", ToastPolicyTests.TheHoldIsMeasuredFromFullOpacity),
        ("extends a repeated toast without restarting its fade", ToastPolicyTests.ARepeatExtendsTheHoldWithoutRestartingTheFade),
        ("swaps a replacement toast in place", ToastPolicyTests.AReplacementSwapsInPlaceRatherThanCrossFading),
        ("dismisses a toast when the full surface takes over", ToastPolicyTests.AFullSurfaceStatusDismissesTheNote),
        ("never steps a toast past either end of its fade", ToastPolicyTests.NeitherEndOfTheFadeCanBePassed),
        ("keeps a shown toast layered", ToastPolicyTests.AShownNoteKeepsASliverOfTranslucency),
        ("expires a toast built without an expiry", ToastPolicyTests.ANoteWithNoExpirySetStillExpires),
        ("forgets a toast that has gone", ToastPolicyTests.AVanishedNoteForgetsWhatItSaid),

        ("keeps a disconnect pressed during a retry disconnected", MirrorConnectionTests.DisconnectDuringARetryStaysDisconnected),
        ("reconnects without running the cable walkthrough", MirrorConnectionTests.ReconnectAfterDisconnectNeverRunsTheWalkthrough),
        ("runs the walkthrough only from a cold start", MirrorConnectionTests.RunsTheWalkthroughOnlyFromAColdStart),
        ("gives up after a bounded number of attempts", MirrorConnectionTests.GivesUpAfterABoundedNumberOfAttempts),
        ("earns a fresh retry budget from a fresh picture", MirrorConnectionTests.AFreshPictureEarnsAFreshRetryBudget),
        ("treats a sleeping phone as asleep, not lost", MirrorConnectionTests.ASleepingPhoneIsNotALostConnection),
        ("tears down exactly once when closing", MirrorConnectionTests.ClosingTearsDownExactlyOnce),
        ("tears down only once when closing twice", MirrorConnectionTests.ClosingTwiceTearsDownOnlyOnce),
        ("bounds how long it keeps trying", MirrorConnectionTests.BoundsHowLongItKeepsTrying),

        ("stops when nothing is moving", AnimationPolicyTests.StopsWhenNothingIsMoving),
        ("runs while anything is moving", AnimationPolicyTests.RunsWhileAnythingIsMoving),
        ("hover easing arrives and stops", AnimationPolicyTests.HoverEasingArrivesAndStops),
        ("hover easing converges downward without overshooting", AnimationPolicyTests.HoverEasingConvergesDownwardWithoutOvershooting),

        ("waits long enough for the stop that matters", TeardownPlanTests.WaitsLongEnoughForTheStopThatMatters),
        ("gives up quickly on a phone that cannot hear it", TeardownPlanTests.GivesUpQuicklyOnAPhoneThatCannotHearIt),
        ("always releases the held touch", TeardownPlanTests.AlwaysReleasesTheHeldTouch),
        ("bounds every teardown path", TeardownPlanTests.BoundsEveryPath),
        ("waits for a goodbye or does not send one", TeardownPlanTests.AGoodbyeIsEitherWaitedForOrNotAttempted),
        ("dispatches every command the client sends", AgentContractTests.AgentDispatchesEveryCommandTheClientSends),
        ("stops the media session from both paths", AgentContractTests.StopsTheMediaSessionFromBothPathsThatEndIt),
        ("sends the stop before forgetting what to stop", AgentContractTests.SendsTheStopBeforeForgettingWhatToStop),

        ("breaks the same way at every display scale", OnboardingLayoutTests.BreaksTheSameWayAtEveryDisplayScale),
        ("sizes type from the window not the display", OnboardingLayoutTests.SizesTypeFromTheWindowNotTheDisplay),
        ("keeps type and space in proportion", OnboardingLayoutTests.KeepsTypeAndSpaceInProportion),
        ("never overlaps the buttons", OnboardingLayoutTests.NeverOverlapsTheButtons),
        ("marks dropped text rather than losing it", OnboardingLayoutTests.MarksDroppedTextRatherThanLosingIt),
        ("reports enough height for every line", OnboardingLayoutTests.ReportsEnoughHeightForEveryLine),
        ("keeps the kicker inside the column", OnboardingLayoutTests.KeepsTheKickerInsideTheColumn),
        ("drops ornament before it drops words", OnboardingLayoutTests.DropsOrnamentBeforeItDropsWords),
        ("centres a page that has no buttons", OnboardingLayoutTests.CentresAPageThatHasNoButtons),
        ("distributes the slack on a page with buttons", OnboardingLayoutTests.DistributesTheSlackOnAPageWithButtons),
        ("never shifts content onto the buttons", OnboardingLayoutTests.NeverShiftsContentOntoTheButtons),
        ("places nothing when there is no column at all", OnboardingLayoutTests.PlacesNothingWhenThereIsNoColumnAtAll),
        ("evens out a headline rather than stranding a word", OnboardingLayoutTests.EvensOutAHeadlineRatherThanStrandingAWord),
        ("leaves a headline that fits alone", OnboardingLayoutTests.LeavesAHeadlineThatFitsAlone),
        ("stacks feature rows without collision", OnboardingLayoutTests.StacksFeatureRowsWithoutCollision),
        ("drops the mark before it drops a feature row", OnboardingLayoutTests.DropsTheMarkBeforeItDropsAFeatureRow),
        ("places a page with no feature rows", OnboardingLayoutTests.PlacesAPageWithNoFeatureRows),
        ("never breaks a headline inside a word", OnboardingLayoutTests.NeverBreaksAHeadlineInsideAWord),

        ("prefers the specific device key over a generic one", JsonFieldLookupTests.PrefersTheSpecificKeyOverAGenericOne),
        ("falls back to the generic device key", JsonFieldLookupTests.FallsBackToTheGenericKey),
        ("prefers the shallower of two matches", JsonFieldLookupTests.PrefersTheShallowerOfTwoMatches),
        ("ignores a non-string match", JsonFieldLookupTests.IgnoresANonStringMatch),
        ("reports no device name rather than guessing", JsonFieldLookupTests.ReportsNothingRatherThanGuessing),

        ("stops moving once the controls are fully shown", ControlStripPolicyTests.FullyShownControlsStopMovingInsteadOfStrobing),
        ("stops moving once the controls are hidden", ControlStripPolicyTests.HiddenControlsStopMovingToo),
        ("keeps the shown controls a sliver short of opaque", ControlStripPolicyTests.ShownControlsKeepASliverOfTranslucency),
        ("arrives exactly without overshooting", ControlStripPolicyTests.FadingArrivesExactlyAndNeverOvershoots),
        ("brings the controls in quicker than it takes them away", ControlStripPolicyTests.ControlsArriveQuickerThanTheyLeave),
        ("keeps the reveal band out of the phone's own reach", ControlStripPolicyTests.KeepsTheRevealBandOutOfThePhonesOwnReach),
        ("shows the controls when the pointer reaches for the top", ControlStripPolicyTests.ShowsWhenThePointerReachesForTheTop),
        ("keeps the controls away while the phone is used", ControlStripPolicyTests.StaysAwayWhileThePhoneIsBeingUsed),
        ("lingers long enough for the controls to be reached", ControlStripPolicyTests.LingersLongEnoughToBeReached),
        ("never hides the controls while one is being used", ControlStripPolicyTests.NeverHidesWhileAControlIsBeingUsed),
        ("hides the controls when the pointer leaves the window", ControlStripPolicyTests.HidesWhenThePointerLeavesTheWindow),
        ("reveals on the same gesture at any window size", ControlStripPolicyTests.MeansTheSameGestureAtAnyWindowSize),

        ("presses Home for a flick off the home indicator", PhoneGestureTests.RecognisesAFlickOffTheHomeIndicator),
        ("presses Home for a flick that drifts slightly", PhoneGestureTests.RecognisesAFlickThatDriftsSlightly),
        ("leaves a mid-screen upward flick as a drag", PhoneGestureTests.IgnoresAFlickThatStartsAwayFromTheEdge),
        ("leaves a twitch on the home indicator as a drag", PhoneGestureTests.IgnoresATwitchOnTheHomeIndicator),
        ("leaves a swipe across the bottom edge as a drag", PhoneGestureTests.IgnoresASidewaysSwipeAlongTheEdge),
        ("leaves a downward drag from the edge as a drag", PhoneGestureTests.IgnoresADownwardSwipeFromTheEdge),

        ("reads one AAC-ELD access unit per audio datagram", AacEldAudioLegTests.ReadsOneAccessUnitPerDatagram),
        ("rejects datagrams that are not the audio payload type", AacEldAudioLegTests.RejectsAnythingThatIsNotTheAudioPayloadType),
        ("counts the audio frames the network lost", AacEldAudioLegTests.CountsTheFramesTheNetworkLost),
        ("treats a reordered audio packet as late rather than lost", AacEldAudioLegTests.TreatsAReorderedPacketAsLateRatherThanLost),
        ("carries the audio sequence wrap into the extended number", AacEldAudioLegTests.CarriesTheSequenceWrapIntoTheExtendedNumber),
        ("states the AAC-ELD config for the negotiated mode", AacEldAudioLegTests.StatesTheAudioSpecificConfigForTheNegotiatedMode),
        ("captures audio access units from the loopback socket", AacEldAudioCaptureTests.WritesLengthPrefixedAccessUnitsFromTheSocket),
        ("requests no audio leg without a capture path", AacEldAudioCaptureTests.StartsNothingWithoutAConfiguredPath),
        ("decodes a recorded audio stream to audible samples", FdkAacEldDecoderTests.DecodesARecordedStreamToAudibleSamples),
        ("opens the default Windows audio output device", FdkAacEldDecoderTests.OpensTheDefaultOutputDevice),

        ("reports streaming while frames arrive", MirrorStreamMonitorTests.ReportsStreamingWhileFramesArrive),
        ("calls a silent stream from a dark phone asleep", MirrorStreamMonitorTests.CallsASilentStreamFromADarkPhoneAsleep),
        ("does not call a still home screen asleep", MirrorStreamMonitorTests.DoesNotCallAStillHomeScreenAsleep),
        ("leaves a long idle session alone", MirrorStreamMonitorTests.LeavesALongIdleSessionAlone),
        ("does not call an awake device asleep over a stream gap", MirrorStreamMonitorTests.DoesNotCallAnAwakeDeviceAsleepWhenTheStreamGapsMomentarily),
        ("still calls a dark picture asleep while the device reports awake", MirrorStreamMonitorTests.StillCallsADarkPictureAsleepWhileTheDeviceReportsAwake),
        ("calls a silent stream with a dead tunnel lost", MirrorStreamMonitorTests.CallsASilentStreamWithADeadTunnelLost),
        ("does not mistake a slept-through gap for a hung decoder", MirrorStreamMonitorTests.DoesNotMistakeASleptThroughGapForAHungDecoder),
        ("reports waking only once", MirrorStreamMonitorTests.ReportsWakingOnlyOnce),
        ("still asks for a keyframe when the picture stalls", MirrorStreamMonitorTests.StillAsksForAKeyframeWhenThePictureStallsWhileAwake),
        ("only probes the tunnel when the stream is silent", MirrorStreamMonitorTests.OnlyProbesTheTunnelWhenTheStreamIsSilent),
        ("calls a streaming but dark picture asleep", MirrorStreamMonitorTests.CallsAStreamingButDarkPictureAsleep),
        ("does not call a brief dark moment sleep", MirrorStreamMonitorTests.DoesNotCallABriefDarkMomentSleep),
        ("wakes when the picture lights up again", MirrorStreamMonitorTests.WakesWhenThePictureLightsUpAgain),

        ("parses the RTP fixed header fields", RtpPacketTests.ParsesFixedHeaderFields),
        ("honours CSRC, header extension and padding when bounding the payload", RtpPacketTests.HonoursCsrcExtensionAndPadding),
        ("rejects malformed RTP datagrams instead of throwing", RtpPacketTests.RejectsMalformedDatagrams),

        ("passes a single-NAL HEVC packet through unchanged", HevcDepacketizerTests.PassesThroughASingleNalPacket),
        ("splits an aggregation packet into its NAL units", HevcDepacketizerTests.SplitsAnAggregationPacketIntoItsNalUnits),
        ("abandons an aggregation packet with an over-long size", HevcDepacketizerTests.RejectsAnAggregationPacketWithABadLength),
        ("reassembles a fragmented NAL unit and restores its type", HevcDepacketizerTests.ReassemblesAFragmentedNalUnit),
        ("discards a fragmented NAL unit whose start was lost", HevcDepacketizerTests.DiscardsAFragmentWhoseStartWasLost),
        ("counts a fragmented NAL unit abandoned mid-way", HevcDepacketizerTests.CountsAFragmentedUnitAbandonedMidWay),
        ("strips Apple's DisplayService NAL trailer", HevcDepacketizerTests.StripsTheDisplayServiceTrailer),
        ("leaves a NAL unit without the trailer untouched", HevcDepacketizerTests.LeavesANalUnitWithoutTheTrailerUntouched),
        ("emits an HEVC access unit only at the RTP marker", HevcAccessUnitAssemblerTests.EmitsOnlyWhenTheMarkerCompletesThePicture),
        ("drops an unfinished access unit when the RTP timestamp changes", HevcAccessUnitAssemblerTests.DropsAnUnfinishedPictureWhenTheTimestampChanges),
        ("drops an HEVC access unit with an RTP sequence gap", HevcAccessUnitAssemblerTests.DropsAPictureWithASequenceGap),
        ("parses HEVC sequence dimensions and DXVA fields", HevcParameterSetTests.ParsesSequenceDimensionsAndDxvaFields),
        ("reads what the stream says about colour range", HevcParameterSetTests.ReadsWhatTheStreamSaysAboutColourRange),
        ("assumes limited range when the stream says nothing", HevcParameterSetTests.AssumesLimitedRangeWhenTheStreamSaysNothing),
        ("reads a matrix that is not BT.709", HevcParameterSetTests.ReadsAMatrixThatIsNotBt709),
        ("parses HEVC picture coding and filter flags", HevcParameterSetTests.ParsesPictureCodingAndFilterFlags),
        ("rejects truncated HEVC parameter sets", HevcParameterSetTests.RejectsTruncatedParameterSets),
        ("builds aligned Annex-B data and DXVA short-slice entries", HevcDxvaPictureDataTests.BuildsAlignedAnnexBAndShortSliceEntries),
        ("refuses an access unit without coded slices", HevcDxvaPictureDataTests.RefusesAnAccessUnitWithoutCodedSlices),
        ("serializes an IDR into the Windows DXVA contract", HevcIdrSubmissionBuilderTests.SerializesAnIdrIntoTheWindowsDxvaContract),
        ("serializes a P-picture reference into the Windows DXVA contract", HevcIdrSubmissionBuilderTests.SerializesAPictureReferenceIntoTheWindowsDxvaContract),
        ("preserves a referenced decoder surface when choosing output", HevcIdrSubmissionBuilderTests.PreservesAReferencedSurfaceWhenSelectingOutput),

        ("bounds latency by dropping the oldest queued frame", LatestFrameQueueTests.BoundsLatencyByDroppingTheOldestFrame),
        ("presents only the newest queued frame", LatestFrameQueueTests.ConsumerTakesOnlyTheNewestFrame),
        ("decodes predictive frames in arrival order", LatestFrameQueueTests.DecoderTakesTheOldestFrame),

        ("creates a driver HEVC decoder and sixteen NV12 output surfaces", D3D11HevcDecoderTests.CreatesDriverDecoderAndSixteenNv12Surfaces),
        ("submits a Main-profile IDR to the hardware decoder", HevcIdrSubmissionBuilderTests.SubmitsTheIdrToTheHardwareDecoder),
        ("decodes an IDR and its following P picture", HevcIdrSubmissionBuilderTests.DecodesAnIdrAndFollowingPPicture),
        ("packs the colour space the way Direct3D reads it", DxgiFlipPresenterTests.PacksTheColourSpaceTheWayDirect3DReadsIt),
        ("decodes and presents through a waitable flip swap chain", DxgiFlipPresenterTests.DecodesAndPresentsThroughAWaitableFlipSwapChain),
        ("stretches studio-range luma to the full display range", DxgiFlipPresenterTests.StretchesStudioRangeLumaToTheFullDisplayRange),
    ];

    private static int Main()
    {
        List<string> failures = [];
        foreach ((string name, Action body) in Tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (SkipException skip)
            {
                Console.WriteLine($"SKIP {name}");
                Console.WriteLine($"     {skip.Message}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Executed {Tests.Length} tests; {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    // ----- Solution boundary (ADR 0014, decision 7) -----

    /// <summary>
    /// Orchard Mirror depends on a Python control-plane agent and a media stack. The Orchard product
    /// is deliberately dependency-free, so Mirror must never be reachable from
    /// <c>Orchard.slnx</c> — otherwise every Orchard build inherits those dependencies. The
    /// prototype was registered there once already, so this guard is not hypothetical.
    /// </summary>
    private static void KeepsMirrorOutOfProductSolution()
    {
        string solution = ReadRepositoryFile("Orchard.slnx");
        Assert(
            !solution.Contains("Orchard.Mirror", StringComparison.OrdinalIgnoreCase),
            "Orchard.slnx references an Orchard.Mirror project. Mirror belongs to Orchard.Mirror.slnx "
                + "so that its Python and media dependencies stay out of the Orchard product.");
    }

    private static void ListsMirrorProjectsInMirrorSolution()
    {
        string solution = ReadRepositoryFile("Orchard.Mirror.slnx");
        foreach (string project in (string[])["Orchard.Mirror.Agent.Windows", "Orchard.Mirror.Probe", "Orchard.Mirror.Shell", "Orchard.Mirror.Video.Windows", "Orchard.Mirror.Windows", "Orchard.Mirror.Tests"])
        {
            Assert(
                solution.Contains(project, StringComparison.Ordinal),
                $"Orchard.Mirror.slnx does not reference {project}, so eng/check-mirror.ps1 would "
                    + "never build or run it.");
        }
    }

    // ----- Harness -----

    private static string ReadRepositoryFile(string relativePath)
    {
        string full = Path.Combine(RepositoryRoot(), relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Expected a repository file at '{full}'.", full);
        }

        return File.ReadAllText(full);
    }

    /// <summary>
    /// Walk up from the test binary until a directory carrying both solutions appears. Anchoring on
    /// the pair rather than on either alone means a partially-applied split cannot resolve to a
    /// directory where the boundary assertions would vacuously pass.
    /// </summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orchard.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "Orchard.Mirror.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static class AgentProtocolTests
    {
        internal static void SerializesRequest()
        {
            string line = AgentResponse.SerializeRequest("42", "connect", new { udid = "phone-1" });
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            Assert(root.GetProperty("version").GetInt32() == 1, "Request version was not 1.");
            Assert(root.GetProperty("id").GetString() == "42", "Request id changed in serialization.");
            Assert(root.GetProperty("command").GetString() == "connect", "Request command changed in serialization.");
            Assert(root.GetProperty("arguments").GetProperty("udid").GetString() == "phone-1", "Request arguments changed in serialization.");
        }

        internal static void ParsesSuccess()
        {
            AgentResponse response = AgentResponse.Parse("{\"version\":1,\"id\":\"7\",\"ok\":true,\"result\":{\"state\":\"connected\"}}");
            Assert(response.Ok, "Successful response was parsed as a failure.");
            Assert(response.Id == "7", "Response id changed while parsing.");
            Assert(response.Result.GetProperty("state").GetString() == "connected", "Response result changed while parsing.");
        }

        internal static void ParsesError()
        {
            AgentResponse response = AgentResponse.Parse("{\"version\":1,\"id\":\"8\",\"ok\":false,\"error\":{\"code\":\"no-device\",\"message\":\"No iPhone is connected over USB.\"}}");
            Assert(!response.Ok, "Failed response was parsed as successful.");
            Assert(response.Error?.Code == "no-device", "Stable agent error code changed while parsing.");
        }

        internal static void RejectsWrongVersion()
        {
            try
            {
                _ = AgentResponse.Parse("{\"version\":2,\"id\":\"9\",\"ok\":true,\"result\":{}}");
            }
            catch (AgentProtocolException)
            {
                return;
            }

            throw new InvalidOperationException("An incompatible agent protocol version was accepted.");
        }

        internal static void NormalizesTouchCoordinates()
        {
            TouchCoordinates topLeft = TouchCoordinates.FromClient(0, 0, 390, 844);
            TouchCoordinates bottomRight = TouchCoordinates.FromClient(389, 843, 390, 844);
            TouchCoordinates center = TouchCoordinates.FromClient(195, 422, 390, 844);
            Assert(topLeft == new TouchCoordinates(0, 0), "The client origin did not map to touchscreen origin.");
            Assert(bottomRight == new TouchCoordinates(65535, 65535), "The client edges did not map to 65535.");
            Assert(center.X is >= 32760 and <= 32900 && center.Y is >= 32760 and <= 32900, "The client center did not map near touchscreen center.");
        }

        internal static void DistinguishesLockStates()
        {
            using JsonDocument locked = JsonDocument.Parse("{\"result\":{\"lockState\":\"locked\"}}");
            using JsonDocument unlocked = JsonDocument.Parse("{\"result\":{\"lockState\":\"unlocked\",\"hasBeenUnlocked\":true}}");
            Assert(CoreDeviceLockState.IsLocked(locked.RootElement), "An explicit locked state was not detected.");
            Assert(!CoreDeviceLockState.IsLocked(unlocked.RootElement), "The word unlocked was mistaken for locked.");
        }
    }

    private static class RtpLoopbackReceiverTests
    {
        internal static void ReceivesDatagram()
        {
            using Orchard.Mirror.Media.RtpLoopbackReceiver receiver = new();
            byte[] expected = Convert.FromHexString("806000010000000111223344AABBCC");
            using System.Net.Sockets.UdpClient sender = new();
            _ = sender.Send(expected, expected.Length, "127.0.0.1", receiver.Port);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            byte[] actual = receiver.ReceiveAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            Assert(actual.AsSpan().SequenceEqual(expected), "The loopback receiver changed or truncated the RTP datagram.");
        }
    }

    /// <summary>Raised by a test whose hardware or fixture is unavailable on this host.</summary>
    internal sealed class SkipException : Exception
    {
        public SkipException(string message)
            : base(message)
        {
        }
    }
}
