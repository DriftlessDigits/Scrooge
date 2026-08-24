using ECommons.DalamudServices;
using System.Speech.Synthesis;

namespace Scrooge;

/// <summary>
/// THE PLUGIN'S VOICE, such as it is: Windows text-to-speech for the two optional
/// "that's done" announcements. Forty lines that had no business sharing a class with
/// a task queue and an ImGui window, and one platform probe that ran in the pinch
/// window's constructor for no reason except that the constructor was there.
/// </summary>
internal static class TtsAnnouncer
{
  /// <summary>
  /// Asks the platform whether it can speak, once at startup, and parks the answer in
  /// <c>Configuration.DontUseTTS</c>. TTS is Windows-only; anywhere else the
  /// SpeechSynthesizer constructor throws and the feature turns itself off rather
  /// than throwing again at every announcement.
  /// </summary>
  internal static void Probe()
  {
    try
    {
      var tts = new SpeechSynthesizer();
      tts.SelectVoice(tts.Voice.Name);
      Plugin.Configuration.DontUseTTS = false;
    }
    catch
    {
      Plugin.Configuration.DontUseTTS = true;
    }
    Plugin.Configuration.Save();
  }

  /// <summary>
  /// Speaks a message, disposing the synthesizer when playback finishes. Returns true
  /// unconditionally: it is enqueued as a task step, and a silent platform must not
  /// wedge the queue behind a voice it does not have.
  /// </summary>
  internal static bool? Speak(string msg)
  {
    if (!Plugin.Configuration.DontUseTTS)
    {
      SpeechSynthesizer tts = new()
      {
        Volume = Plugin.Configuration.TTSVolume
      };
      tts.SpeakAsync(msg);
      tts.SpeakCompleted += (o, e) =>
      {
        tts.Dispose();
        Svc.Log.Verbose($"Finished message: {msg} - tts disposed");
      };
    }
    return true;
  }
}
