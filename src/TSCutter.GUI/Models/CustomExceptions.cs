using System;

namespace TSCutter.GUI.Models;

public class TooManyDecodeFailuresException(string message) : Exception(message);

public sealed class NoVideoStreamException()
    : Exception("The input file does not contain a video stream.");
