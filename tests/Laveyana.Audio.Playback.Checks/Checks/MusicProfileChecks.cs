using Laveyana.Audio.Playback;

internal static class MusicProfileChecks
{
    public static void Run()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { _ = new RhythmBandEvidence([],100,cancelled.Token); throw new InvalidOperationException("Cancelled empty band analysis succeeded."); }
            catch (OperationCanceledException) { }
            var empty = new RhythmBandEvidence([],100,default);
            Require(empty.Measure(0,0).Values.Length == 0,"Empty band evidence invented a sample.");
            try { _ = empty.ChoosePhase(0,50,120,0,100,true,cancelled.Token); throw new InvalidOperationException("Cancelled phase shortcut succeeded."); }
            catch (OperationCanceledException) { }
        }
        var signal = Enumerable.Range(0,1000).Select(i => new RhythmEnergy(i%50==0?1f:0f, i%25==0?.5f:0f,0)).ToArray();
        var evidence = new RhythmBandEvidence(signal,100,default);
        var full = evidence.Measure(0,signal.Length);
        foreach (var pair in new (double A,double B)[] { (120,120),(120,121),(120,125),(40,240) })
        {
            var selective = evidence.MeasurePair(0,signal.Length,pair.A,pair.B);
            Require(Math.Abs(selective.First-full.AtBpm(pair.A))<1e-12 && Math.Abs(selective.Second-full.AtBpm(pair.B))<1e-12,
                "Shared lag probes changed the periodicity score.");
        }
        var loud = new TrackLoudnessAnalyzer();
        var quiet = new TrackLoudnessAnalyzer();
        var silence = new TrackLoudnessAnalyzer();
        float[] block = new float[9600], half = new float[9600];
        for(int n=0;n<60;n++)
        {
            for(int i=0;i<4800;i++)
            {
                float v=(float)(.25*Math.Sin(Math.Tau*1000*(n*4800L+i)/48000));
                block[2*i]=block[2*i+1]=v;
                half[2*i]=half[2*i+1]=v*.5f;
            }
            loud.Add(block);quiet.Add(half);silence.Add(new float[9600]);
        }
        var a=loud.Finish();var b=quiet.Finish();
        Require(a.IntegratedLufs is float al&&b.IntegratedLufs is float bl&&Math.Abs(al-bl-6.0206)<.01 && Math.Abs(al+12.0345)<.03,
            "Loudness did not scale by 6 dB.");
        Require(a.SamplePeakDbfs is float peak&&Math.Abs(peak+12.0412)<.01&&
            a.ShortTerm.Length==4&&a.IsValid(288000)&&silence.Finish().IntegratedLufs is null,
            "Loudness peak, short-term windows or silence failed.");
        Require(a.RecommendedGainDb() is float gain&&gain<=6&&gain+a.SamplePeakDbfs.GetValueOrDefault()<=-3+.001,
            "Suggested gain exceeds sample-peak headroom.");
        // Different chunk boundaries must not change any loudness measurement.
        var chunked=new TrackLoudnessAnalyzer();
        for(int n=0;n<60;n++)
        {
            for(int i=0;i<4800;i++)
            {
                float v=(float)(.25*Math.Sin(Math.Tau*1000*(n*4800L+i)/48000));
                block[2*i]=block[2*i+1]=v;
            }
            for(int i=0;i<block.Length;i+=246)
                chunked.Add(block.AsSpan(i,Math.Min(246,block.Length-i)));
        }
        var split=chunked.Finish();
        Require(a.IntegratedLufs==split.IntegratedLufs&&a.SamplePeakDbfs==split.SamplePeakDbfs&&
            a.LoudnessRange==split.LoudnessRange&&a.ShortTerm.SequenceEqual(split.ShortTerm),
            "LUFS depended on PCM chunk boundaries.");
        try { chunked.Add([.1f]); throw new InvalidOperationException("Partial loudness stereo frame accepted."); }
        catch(ArgumentException) { }
        var points=Enumerable.Range(0,400).Select(i=>new WaveformPoint(.6f,i%12==0?.6f:.2f,.2f,.1f)).ToArray();
        // 125 BPM grid, 4 pulses/bar, exact 40ms peak bins.
        const int hop=1920, beatHop=12, count=32;
        long[] beats=Enumerable.Range(0,count).Select(i=>(long)i*beatHop*hop).ToArray();
        for(int i=0;i<count;i++)points[i*beatHop]=new(.7f,i%4==0?.7f:.18f,.2f,.1f);
        var rhythm=new TrackBeatGrid(125,0,.8f,beats)
        {BeatFrames=beats,Sections=[new(0,400L*hop,125,.8f,count,count)]};
        var waveform=new TrackWaveform(points,400L*hop,hop){Rhythm=rhythm};
        var profile=TrackMusicProfileAnalyzer.Derive(waveform);
        Require(profile.IsValid(waveform.TotalFrames)&&profile.Meter.Length==1&&
            profile.Meter[0].PulsesPerBar==4&&profile.Meter[0].Downbeats.SequenceEqual(beats.Where((_,i)=>i%4==0)),
            "Distinct repeated accents did not produce the expected meter hypothesis.");
        var invalidMeter = waveform with { Rhythm = rhythm with { Sections = [rhythm.Sections[0] with { Bpm = double.NaN }] } };
        Require(TrackMusicProfileAnalyzer.Derive(invalidMeter).Meter.Length == 0,
            "A nonfinite tempo produced a meter hypothesis.");
        var flat=waveform with{Points=points.Select(_=>new WaveformPoint(.6f,.3f,.2f,.1f)).ToArray()};
        Require(TrackMusicProfileAnalyzer.Derive(flat).Meter.Length==0,"Uniform accents invented a bar.");
        var gapBeats=beats.Where((_,i)=>i!=10).ToArray();
        var gap=waveform with{Rhythm=rhythm with{BeatFrames=gapBeats}};
        Require(TrackMusicProfileAnalyzer.Derive(gap).Meter.Length==0,
            "A missing beat rotated the inferred bar phase.");
        Require(!(profile with{Meter=[profile.Meter[0] with{Downbeats=[0,0]}]}).IsValid(waveform.TotalFrames),
            "Duplicate downbeat passed profile validation.");
        Require(!(profile with{LocalKeyVersion=int.MaxValue}).IsValid(waveform.TotalFrames),
            "Unknown future profile version passed validation.");
        Require(!(a with{ShortTerm=[a.ShortTerm[0],a.ShortTerm[0]]}).IsValid(288000),
            "Duplicate loudness timestamp passed validation.");
        Require(ReferenceEquals(profile,TrackMusicProfileAnalyzer.Derive(waveform,profile)),
            "A current derived profile was recomputed.");
        var key=new TrackMusicalKey(9,MusicalMode.Minor,.8f,.9f,.2f,8,10);
        var withKey=waveform with{MusicalKey=key,MusicProfile=profile with{LocalKeys=[new(0,10000,key)]}};
        var match=MixCompatibilityAnalyzer.Compare(withKey,0,withKey,0);
        Require(match.Score is float score&&score>.99f&&match.Evidence>.99f,"Identical tracks were not compatible.");
        var halfTempo = new TrackWaveform([], waveform.TotalFrames, hop)
        {
            Rhythm = new TrackBeatGrid(62.5,0,.8f,[]) { BeatFrames = [0,46080,92160,138240] }
        };
        Require(MixCompatibilityAnalyzer.Compare(withKey,0,halfTempo,0).Tempo is float halfScore && halfScore > .999f &&
            MixCompatibilityAnalyzer.Compare(halfTempo,0,withKey,0).Tempo is float doubleScore && doubleScore > .999f,
            "Octave-equivalent tempo compatibility changed.");
        var uncertain=withKey with{MusicProfile=profile with{LocalKeys=[new(0,10000,null)]}};
        Require(MixCompatibilityAnalyzer.Compare(withKey,0,uncertain,0).Harmony is null,
            "An uncertain local key was incorrectly replaced by a global harmony match.");
        Require(MixCompatibilityAnalyzer.Compare(withKey,-1,withKey,0).Score is null,
            "An out-of-range transition was scored.");
        var invalidEnergy=withKey with{MusicProfile=profile with
        {Energy=[new(0,waveform.TotalFrames,float.NaN,0,0)]}};
        Require(MixCompatibilityAnalyzer.Compare(withKey,0,invalidEnergy,0).Energy is null,
            "A nonfinite energy value contaminated compatibility.");
        var restored=TrackMusicProfileAnalyzer.Derive(waveform,profile with{EnergyVersion=1});
        Require(restored.EnergyVersion==TrackMusicProfile.CurrentEnergyVersion&&ReferenceEquals(restored.Meter,profile.Meter),
            "Updating energy forced a redundant meter analysis.");
        var malformed=TrackMusicProfileAnalyzer.Derive(waveform,profile with{Energy=null!});
        Require(malformed.IsValid(waveform.TotalFrames),"Invalid current-version profile was reused.");
        // Two 40ms points, but only one frame represented by the final loud bin.
        var partial=new TrackWaveform([new(.1f,0,.1f,0),new(1,1,0,0)],1921,1920);
        var partialProfile=TrackMusicProfileAnalyzer.Derive(partial);
        Require(partialProfile.Energy.Length==1&&partialProfile.Energy[0].BassShare<.01f,
            "Partial waveform interval was lost.");
        var malformedPcm=TrackWaveform.Analyze(new MalformedPcm(),default);
        Require(malformedPcm.Points.All(p=>float.IsFinite(p.Peak)&&p.Peak<=1&&
            float.IsFinite(p.Low)&&float.IsFinite(p.Mid)&&float.IsFinite(p.High))&&
            malformedPcm.MusicProfile!.IsValid(malformedPcm.TotalFrames),
            "Extreme custom PCM poisoned analysis.");
        var metrics=RhythmReportReplay.Metrics([0,48000,96000,200000],[0,48000,96000],4800);
        Require(metrics.F1==1&&metrics.MaeMs==0,"Unannotated tail biased regression metrics.");
        Console.WriteLine("OK: loudness, conservative meter, derived cache, compatibility and excerpt metrics.");
    }

    public static void CheckNormalization(CachedTrack track, CachedTrack next)
    {
        var normalized = new CachedTrack(track.Name, track.Path, track.Waveform with
        {
            MusicProfile = new TrackMusicProfile { Loudness = new TrackLoudness(-8,-6,0,[]) }
        });
        using var queue = new QueueSource();
        queue.SetRepeatOne(true);
        queue.SetLoudnessNormalization(true);
        queue.SetFirst(normalized);
        float[] block = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
        Require(queue.Read(block)>0&&Math.Abs(block[0]-.25*Math.Pow(10,-6d/20))<.0002,
            "Measured gain was not applied to the current deck.");
        queue.SetLoudnessNormalization(false);
        for(int i=0;i<30;i++)Require(queue.Read(block)>0,"Normalization interrupted playback.");
        Require(Math.Abs(block[0]-.25)<.0002,"Disabling normalization did not restore unity gain.");
        using var large = new QueueSource();
        large.SetAutoMix(false);
        large.SetFirst(track);
        float[] oversized = new float[PlaybackFormat.BlockFrames*PlaybackFormat.Channels*3];
        Require(large.Read(oversized)==oversized.Length/PlaybackFormat.Channels,
            "Oversized direct read exceeded the PCM scratch buffer.");
        large.SetCrossfadeDuration(100);
        Require(large.SetNext(next)&&large.RequestMix(),"Large-block mix was not armed.");
        Require(large.Read(oversized)==oversized.Length/PlaybackFormat.Channels &&
            oversized.All(float.IsFinite),"Oversized mix exceeded the PCM scratch buffer.");
        using var reader = new CachedPcmSource(track.Path,new DeckCursor(track.Waveform.TotalFrames,0));
        Array.Fill(oversized, -2f);
        Require(reader.ReadAtRate(oversized,1.25)==PlaybackFormat.BlockFrames &&
            oversized[PlaybackFormat.BlockFrames*PlaybackFormat.Channels]==-2f,
            "A direct oversized rate read exceeded the PCM buffer or wrote its unused tail.");
        try { reader.ReadAtRate(oversized,double.NaN); throw new InvalidOperationException("Invalid rate accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("OK: optional deck loudness gain, smooth bypass and bounded large reads.");
    }

    private sealed class MalformedPcm : Laveyana.Audio.IPcmSource
    {
        private bool _read;
        public int SampleRate=>48000;
        public int Read(Span<float> destination)
        {
            if(_read)return 0;
            _read=true;
            for(int i=0;i<destination.Length;i++)
                destination[i]=(i%4) switch{0=>float.NaN,1=>float.MaxValue,2=>float.NegativeInfinity,_=>-float.MaxValue};
            return destination.Length/2;
        }
    }

    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
}
