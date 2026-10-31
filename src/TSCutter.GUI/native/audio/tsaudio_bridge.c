/*
 * tsaudio_bridge.c - Minimal PCM playback bridge over miniaudio for TSCutter.GUI.
 *
 * Scope: this bridge only feeds interleaved float32 PCM frames to the default
 * audio output device. Demuxing, decoding, channel downmixing and resampling
 * stay in the application's FFmpeg stack (libswresample). The device callback
 * performs bounded, allocation-free copies from a lock-free SPSC ring buffer
 * and never calls back into managed code, so no exception can cross the ABI.
 *
 * The consumed-frames counter doubles as the audio master clock: it advances
 * for every frame the device renders, including the silence substituted on
 * underflow, so it always reflects what the listener actually hears.
 */

#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MA_NO_GENERATION
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <stddef.h>
#include <string.h>

/* Status codes shared with the managed wrapper. */
enum
{
    TSAUDIO_OK = 0,
    TSAUDIO_ERR_BAD_ARGS = 1,
    TSAUDIO_ERR_NO_DEVICE = 2,
    TSAUDIO_ERR_INIT_FAILED = 3
};

typedef struct tsaudio_device
{
    ma_device device;
    ma_rb ring;
    ma_atomic_bool32 device_failed;     /* The audio thread stopped on its own. */
    ma_atomic_uint64 consumed_frames;   /* Frames rendered by the device. */
    ma_atomic_uint64 underflow_frames;  /* Silence frames caused by an empty ring. */
} tsaudio_device;

static void tsaudio_data_callback(ma_device* pDevice, void* pOutput, const void* pInput,
    ma_uint32 frameCount)
{
    (void)pInput;
    tsaudio_device* dev = (tsaudio_device*)pDevice->pUserData;
    const size_t frame_bytes = (size_t)pDevice->playback.channels * sizeof(float);
    size_t remaining_bytes = (size_t)frameCount * frame_bytes;
    unsigned char* out = (unsigned char*)pOutput;

    if (dev == NULL)
    {
        memset(pOutput, 0, remaining_bytes);
        return;
    }

    while (remaining_bytes > 0)
    {
        size_t chunk_bytes = remaining_bytes;
        void* source = NULL;
        if (ma_rb_acquire_read(&dev->ring, &chunk_bytes, &source) != MA_SUCCESS ||
            source == NULL || chunk_bytes == 0)
            break;
        memcpy(out, source, chunk_bytes);
        ma_rb_commit_read(&dev->ring, chunk_bytes);
        out += chunk_bytes;
        remaining_bytes -= chunk_bytes;
    }

    if (remaining_bytes > 0)
    {
        memset(out, 0, remaining_bytes);
        ma_atomic_uint64_fetch_add(&dev->underflow_frames, remaining_bytes / frame_bytes);
    }

    ma_atomic_uint64_fetch_add(&dev->consumed_frames, (ma_uint64)frameCount);
}

#if defined(TSAUDIO_STATIC)
#define TSAUDIO_EXPORT
#elif defined(_WIN32)
#define TSAUDIO_EXPORT __declspec(dllexport)
#else
#define TSAUDIO_EXPORT __attribute__((visibility("default")))
#endif

TSAUDIO_EXPORT void* tsaudio_open(int sample_rate, int channels, int ring_capacity_frames,
    int* error_code)
{
    if (error_code != NULL)
        *error_code = TSAUDIO_ERR_BAD_ARGS;
    if (sample_rate <= 0 || channels <= 0 || channels > 32 || ring_capacity_frames <= 0)
        return NULL;

    tsaudio_device* dev = (tsaudio_device*)ma_malloc(sizeof(*dev), NULL);
    if (dev == NULL)
    {
        if (error_code != NULL)
            *error_code = TSAUDIO_ERR_INIT_FAILED;
        return NULL;
    }
    memset(dev, 0, sizeof(*dev));
    ma_atomic_bool32_set(&dev->device_failed, MA_FALSE);
    ma_atomic_uint64_set(&dev->consumed_frames, 0);
    ma_atomic_uint64_set(&dev->underflow_frames, 0);

    const size_t ring_bytes = (size_t)ring_capacity_frames * (size_t)channels * sizeof(float);
    if (ma_rb_init(ring_bytes, NULL, NULL, &dev->ring) != MA_SUCCESS)
    {
        ma_free(dev, NULL);
        if (error_code != NULL)
            *error_code = TSAUDIO_ERR_INIT_FAILED;
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32;
    config.playback.channels = (ma_uint32)channels;
    config.sampleRate = (ma_uint32)sample_rate;
    config.dataCallback = tsaudio_data_callback;
    config.pUserData = dev;

    const ma_result result = ma_device_init(NULL, &config, &dev->device);
    if (result != MA_SUCCESS)
    {
        ma_rb_uninit(&dev->ring);
        ma_free(dev, NULL);
        if (error_code != NULL)
        {
            *error_code = (result == MA_NO_BACKEND || result == MA_NO_DEVICE ||
                           result == MA_FAILED_TO_INIT_BACKEND ||
                           result == MA_FAILED_TO_OPEN_BACKEND_DEVICE)
                ? TSAUDIO_ERR_NO_DEVICE
                : TSAUDIO_ERR_INIT_FAILED;
        }
        return NULL;
    }

    if (error_code != NULL)
        *error_code = TSAUDIO_OK;
    return dev;
}

TSAUDIO_EXPORT void tsaudio_close(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL)
        return;
    ma_device_uninit(&dev->device);
    ma_rb_uninit(&dev->ring);
    ma_free(dev, NULL);
}

TSAUDIO_EXPORT int tsaudio_start(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL)
        return TSAUDIO_ERR_BAD_ARGS;
    return ma_device_start(&dev->device) == MA_SUCCESS ? TSAUDIO_OK : TSAUDIO_ERR_INIT_FAILED;
}

TSAUDIO_EXPORT int tsaudio_stop(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL)
        return TSAUDIO_ERR_BAD_ARGS;
    return ma_device_stop(&dev->device) == MA_SUCCESS ? TSAUDIO_OK : TSAUDIO_ERR_INIT_FAILED;
}

TSAUDIO_EXPORT int tsaudio_is_playing(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL)
        return 0;
    return ma_device_get_state(&dev->device) == ma_device_state_started ? 1 : 0;
}

/* Apply gain at device output, including PCM buffered before a pause. */
TSAUDIO_EXPORT void tsaudio_set_volume(void* handle, float volume)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev != NULL)
        ma_device_set_master_volume(&dev->device, volume);
}

/* Drop buffered PCM and keep the device running. Stop/start is synchronous in
   miniaudio, so the callback cannot observe the ring reset mid-flight. */
TSAUDIO_EXPORT int tsaudio_flush(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL)
        return TSAUDIO_ERR_BAD_ARGS;
    if (ma_device_get_state(&dev->device) != ma_device_state_started)
    {
        ma_rb_reset(&dev->ring);
        return TSAUDIO_OK;
    }
    ma_device_stop(&dev->device);
    ma_rb_reset(&dev->ring);
    return ma_device_start(&dev->device) == MA_SUCCESS ? TSAUDIO_OK : TSAUDIO_ERR_INIT_FAILED;
}

/* Returns the number of frames accepted; the ring may be near full, in which
   case the producer waits and retries. */
TSAUDIO_EXPORT int tsaudio_write(void* handle, const float* frames, int frame_count)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    if (dev == NULL || frames == NULL || frame_count <= 0)
        return 0;

    const size_t frame_bytes = (size_t)dev->device.playback.channels * sizeof(float);
    size_t remaining_bytes = (size_t)frame_count * frame_bytes;
    const unsigned char* in = (const unsigned char*)frames;

    while (remaining_bytes > 0)
    {
        size_t chunk_bytes = remaining_bytes;
        void* dest = NULL;
        if (ma_rb_acquire_write(&dev->ring, &chunk_bytes, &dest) != MA_SUCCESS ||
            dest == NULL || chunk_bytes == 0)
            break;
        memcpy(dest, in, chunk_bytes);
        ma_rb_commit_write(&dev->ring, chunk_bytes);
        in += chunk_bytes;
        remaining_bytes -= chunk_bytes;
    }

    return (int)(((size_t)frame_count * frame_bytes - remaining_bytes) / frame_bytes);
}

TSAUDIO_EXPORT unsigned long long tsaudio_consumed_frames(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    return dev != NULL ? ma_atomic_uint64_get(&dev->consumed_frames) : 0;
}

TSAUDIO_EXPORT unsigned long long tsaudio_underflow_frames(void* handle)
{
    tsaudio_device* dev = (tsaudio_device*)handle;
    return dev != NULL ? ma_atomic_uint64_get(&dev->underflow_frames) : 0;
}
