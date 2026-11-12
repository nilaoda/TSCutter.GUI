# Remove mode

Remove mode marks sections to discard, such as commercials or opening credits, and joins the rest into one file. It works with either keyframe cutting or smart cut.

## Steps

1. Turn on **Remove** in the toolbar, to the left of **Smart cut**. The setting is remembered across launches.
2. Mark ranges to remove as you would mark clips: the start is the first removed frame and the end is the frame where playback resumes, which is kept. A range without an end is removed up to the end of the file.
3. Select the ranges to remove and click **Export without ranges**; the rest is joined into one file.

<Screenshot name="remove" alt="Remove mode with two marked ranges to discard" />

## Display and totals

Ranges to remove are shown in red on the timeline, the frame strip and the clip cards (the resume frame stays unmarked). Clip cards show the removed size (N/A for sources that are remuxed), and the summary shows the number of ranges, the remaining duration and the estimated size, replaced by the actual output size after export.

## Notes

- In keyframe cut mode both ends fall on keyframes, so an open-GOP source may lose a few leading pictures at a removal start; smart cut mode removes exact frames.
- Clips cannot be saved, queued or exported separately in remove mode.
- The mode is saved with projects (project version 6, which older versions cannot open).
