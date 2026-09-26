using System;

namespace TSCutter.GUI.Models;

public class TooManyDecodeFailuresException(string message) : Exception(message);

public sealed class NoVideoStreamException()
    : Exception("The input file does not contain a video stream.");

public sealed class ScrambledTsException()
    : Exception("The input contains scrambled TS payload. Decrypt it before opening it for video preview.");

public sealed class MediaReadTimeoutException()
    : TimeoutException("Reading the video timed out. Try seeking to another position or check whether the file is encrypted or damaged.");
