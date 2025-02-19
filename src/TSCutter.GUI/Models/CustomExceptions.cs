using System;

namespace TSCutter.GUI.Models;

public class TooManyDecodeFailuresException(string message) : Exception(message);