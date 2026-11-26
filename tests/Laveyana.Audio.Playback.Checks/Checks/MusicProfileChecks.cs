using Laveyana.Audio.Playback;

internal static class MusicProfileChecks
{
    public static void Run()
    {
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
        var uncertain=withKey with{MusicProfile=profile with{LocalKeys=[new(0,10000,null)]}};
        Require(MixCompatibilityAnalyzer.Compare(withKey,0,uncertain,0).Harmony is null,
            "An uncertain local key was incorrectly replaced by a global harmony match.");
        Require(MixCompatibilityAnalyzer.Compare(withKey,-1,withKey,0).Score is null,
            "An out-of-range transition was scored.");
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

    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
}
