using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public static class TsElementaryStreamUtil
{
    public static string GetFileExtension(
        byte streamType,
        TsMpegAudioLayer? mpegAudioLayer = null,
        TsSupplementaryStreamType? supplementaryStreamType = null)
    {
        if (mpegAudioLayer is not null)
        {
            return mpegAudioLayer switch
            {
                TsMpegAudioLayer.LayerI => ".mp1",
                TsMpegAudioLayer.LayerII => ".mp2",
                TsMpegAudioLayer.LayerIII => ".mp3",
                _ => ".bin"
            };
        }

        if (supplementaryStreamType is not null)
        {
            return supplementaryStreamType switch
            {
                TsSupplementaryStreamType.Ac4 => ".ac4",
                TsSupplementaryStreamType.Opus => ".opus",
                TsSupplementaryStreamType.Smpte302M => ".s302m",
                TsSupplementaryStreamType.Dra => ".dra",
                TsSupplementaryStreamType.SmpteKlv => ".klv",
                TsSupplementaryStreamType.TimedId3 => ".id3",
                _ => ".bin"
            };
        }

        return streamType switch
        {
            TsStreamTypes.Mpeg1Video => ".m1v",
            TsStreamTypes.Mpeg2Video => ".m2v",
            TsStreamTypes.Mpeg1Audio => ".mp1",
            TsStreamTypes.Mpeg2Audio => ".mp2",
            TsStreamTypes.Aac => ".aac",
            TsStreamTypes.Mpeg4Video => ".m4v",
            TsStreamTypes.AacLatm => ".latm",
            TsStreamTypes.H264 => ".h264",
            TsStreamTypes.Mpeg4Audio => ".aac",
            TsStreamTypes.Mvc => ".h264",
            TsStreamTypes.Hevc => ".h265",
            TsStreamTypes.Vvc => ".h266",
            TsStreamTypes.Cavs => ".avs",
            TsStreamTypes.Ac3 => ".ac3",
            TsStreamTypes.Dts => ".dts",
            TsStreamTypes.TrueHd => ".thd",
            TsStreamTypes.Eac3 or TsStreamTypes.Eac3Atsc or TsStreamTypes.Eac3Secondary => ".eac3",
            TsStreamTypes.DtsHd or TsStreamTypes.DtsHdMaster => ".dtshd",
            TsStreamTypes.Dirac => ".drc",
            TsStreamTypes.Avs2 => ".avs2",
            TsStreamTypes.Avs3 => ".avs3",
            TsStreamTypes.Av3a => ".av3a",
            TsStreamTypes.Vc1 => ".vc1",
            _ => ".bin"
        };
    }
}
