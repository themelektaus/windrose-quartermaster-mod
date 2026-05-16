// Quartermaster Bink Audio Encoder CLI
// Wraps Epic's UECompressBinkAudio() (UE5.7 Engine/Source/Runtime/BinkAudioDecoder/SDK/BinkAudio).
//
// Usage:
//   binkaudioenc.exe <input.wav> <output.binka> [-q <0..9>]
//
// Input:  16-bit PCM WAV (interleaved, mono or stereo, 22050/44100/48000 Hz).
// Output: Raw encoder buffer [BinkAudioFileHeader][SeekTable (uint16 deltas)][Frames].
//         This is byte-identical to what AudioFormatBink::Cook() produces inside UE5.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <vector>

#include "binka_ue_encode.h"

static void* AllocThunk(uintptr_t Bytes) { return malloc((size_t)Bytes); }
static void  FreeThunk(void* Ptr)        { free(Ptr); }

#pragma pack(push, 1)
struct WavRiffHeader { char Riff[4]; uint32_t FileSize; char Wave[4]; };
struct WavChunkHead  { char Tag[4]; uint32_t Size; };
struct WavFmtChunk
{
    uint16_t Format;        // 1 = PCM, 3 = IEEE float, 0xFFFE = WAVE_FORMAT_EXTENSIBLE
    uint16_t Channels;
    uint32_t SampleRate;
    uint32_t ByteRate;
    uint16_t BlockAlign;
    uint16_t BitsPerSample;
};
#pragma pack(pop)

static bool ReadWav(const char* Path, std::vector<int16_t>& OutPcm, uint32_t& OutRate, uint8_t& OutChannels)
{
    FILE* F = nullptr;
    if (fopen_s(&F, Path, "rb") != 0 || !F)
    {
        fprintf(stderr, "[ERR] Cannot open input: %s\n", Path);
        return false;
    }

    WavRiffHeader Riff = {};
    if (fread(&Riff, 1, sizeof(Riff), F) != sizeof(Riff)
        || memcmp(Riff.Riff, "RIFF", 4) != 0
        || memcmp(Riff.Wave, "WAVE", 4) != 0)
    {
        fprintf(stderr, "[ERR] Not a RIFF/WAVE file.\n");
        fclose(F);
        return false;
    }

    bool HaveFmt = false;
    WavFmtChunk Fmt = {};
    std::vector<uint8_t> PcmRaw;

    while (true)
    {
        WavChunkHead Head = {};
        size_t Got = fread(&Head, 1, sizeof(Head), F);
        if (Got == 0) break;
        if (Got != sizeof(Head)) { fprintf(stderr, "[ERR] Truncated chunk header.\n"); fclose(F); return false; }

        if (memcmp(Head.Tag, "fmt ", 4) == 0)
        {
            if (Head.Size < sizeof(WavFmtChunk)) { fprintf(stderr, "[ERR] fmt  chunk too small.\n"); fclose(F); return false; }
            if (fread(&Fmt, 1, sizeof(Fmt), F) != sizeof(Fmt)) { fclose(F); return false; }
            // Skip any trailing fmt bytes (WAVE_FORMAT_EXTENSIBLE etc.)
            if (Head.Size > sizeof(WavFmtChunk))
                fseek(F, (long)(Head.Size - sizeof(WavFmtChunk)), SEEK_CUR);
            HaveFmt = true;
        }
        else if (memcmp(Head.Tag, "data", 4) == 0)
        {
            if (!HaveFmt) { fprintf(stderr, "[ERR] data chunk before fmt chunk.\n"); fclose(F); return false; }
            PcmRaw.resize(Head.Size);
            if (fread(PcmRaw.data(), 1, Head.Size, F) != Head.Size) { fclose(F); return false; }
            break;
        }
        else
        {
            // Skip unknown chunk (LIST/bext/...). Chunks are padded to even sizes per RIFF spec.
            uint32_t Skip = Head.Size + (Head.Size & 1u);
            fseek(F, (long)Skip, SEEK_CUR);
        }
    }

    fclose(F);

    if (!HaveFmt || PcmRaw.empty()) { fprintf(stderr, "[ERR] Missing fmt / data chunk.\n"); return false; }
    if (Fmt.Format != 1) { fprintf(stderr, "[ERR] Only PCM (format=1) supported. Got format=%u.\n", Fmt.Format); return false; }
    if (Fmt.BitsPerSample != 16) { fprintf(stderr, "[ERR] Only 16-bit PCM supported. Got %u-bit.\n", Fmt.BitsPerSample); return false; }
    if (Fmt.Channels < 1 || Fmt.Channels > 16) { fprintf(stderr, "[ERR] Channel count out of range (1-16). Got %u.\n", Fmt.Channels); return false; }

    OutRate = Fmt.SampleRate;
    OutChannels = (uint8_t)Fmt.Channels;
    OutPcm.assign((int16_t*)PcmRaw.data(), (int16_t*)(PcmRaw.data() + PcmRaw.size()));
    return true;
}

int main(int Argc, char** Argv)
{
    if (Argc < 3)
    {
        fprintf(stderr, "Usage: binkaudioenc.exe <input.wav> <output.binka> [-q <0..9>]\n");
        fprintf(stderr, "  Quality: 0 = best, 4 = UE default, 9 = worst (below 4 is rough)\n");
        return 1;
    }

    const char* InputPath = Argv[1];
    const char* OutputPath = Argv[2];
    uint8_t Quality = 4; // UE Quality=100 maps to Bink=0; UE Quality=1 maps to Bink=4. We pick mid-default.

    for (int I = 3; I < Argc; ++I)
    {
        if (strcmp(Argv[I], "-q") == 0 && I + 1 < Argc)
        {
            int Q = atoi(Argv[++I]);
            if (Q < 0) Q = 0; if (Q > 9) Q = 9;
            Quality = (uint8_t)Q;
        }
    }

    std::vector<int16_t> Pcm;
    uint32_t SampleRate = 0;
    uint8_t Channels = 0;
    if (!ReadWav(InputPath, Pcm, SampleRate, Channels)) return 2;

    fprintf(stdout, "[OK] WAV: rate=%u ch=%u samples=%zu (%.2f s) quality=%u\n",
        SampleRate, Channels, Pcm.size() / Channels,
        (double)(Pcm.size() / Channels) / (double)SampleRate, Quality);

    // Per AudioFormatBink::GetMaxSeekTableEntries(): UE caps at 4096 by default, raises if file needs more.
    // We always supply a generous cap. UECompressBinkAudio will pick what it needs.
    uint16_t SeekTableMaxEntries = 4096;
    // Allow up to 65535 if the file is very long.
    {
        uint32_t MaxFrameSamples = (SampleRate >= 44100) ? 1920 : (SampleRate >= 22050 ? 960 : 480);
        uint64_t TotalFrames = (Pcm.size() / Channels);
        uint64_t Entries = TotalFrames / MaxFrameSamples;
        if (Entries > 4096) SeekTableMaxEntries = (Entries > 65535) ? 65535 : (uint16_t)Entries;
    }

    void* Compressed = nullptr;
    uint32_t CompressedLen = 0;

    uint8_t Err = UECompressBinkAudio(
        Pcm.data(),
        (uint32_t)(Pcm.size() * sizeof(int16_t)),
        SampleRate,
        Channels,
        Quality,
        /*GenerateSeekTable=*/1,
        SeekTableMaxEntries,
        AllocThunk, FreeThunk,
        &Compressed, &CompressedLen);

    if (Err != BINKA_COMPRESS_SUCCESS || Compressed == nullptr)
    {
        const char* Msg = "Unknown error";
        switch (Err)
        {
            case BINKA_COMPRESS_ERROR_CHANS:      Msg = "Invalid channel count"; break;
            case BINKA_COMPRESS_ERROR_SAMPLES:    Msg = "No sample data"; break;
            case BINKA_COMPRESS_ERROR_RATE:       Msg = "Invalid sample rate"; break;
            case BINKA_COMPRESS_ERROR_QUALITY:    Msg = "Invalid quality"; break;
            case BINKA_COMPRESS_ERROR_ALLOCATORS: Msg = "No allocators"; break;
            case BINKA_COMPRESS_ERROR_OUTPUT:     Msg = "No output pointers"; break;
            case BINKA_COMPRESS_ERROR_SEEKTABLE:  Msg = "Invalid seek table size"; break;
            case BINKA_COMPRESS_ERROR_SIZE:       Msg = "Input file too large"; break;
        }
        fprintf(stderr, "[ERR] UECompressBinkAudio failed: %s (code %u)\n", Msg, Err);
        return 3;
    }

    FILE* Out = nullptr;
    if (fopen_s(&Out, OutputPath, "wb") != 0 || !Out)
    {
        fprintf(stderr, "[ERR] Cannot open output: %s\n", OutputPath);
        FreeThunk(Compressed);
        return 4;
    }

    if (fwrite(Compressed, 1, CompressedLen, Out) != CompressedLen)
    {
        fprintf(stderr, "[ERR] Short write.\n");
        fclose(Out);
        FreeThunk(Compressed);
        return 5;
    }
    fclose(Out);
    FreeThunk(Compressed);

    fprintf(stdout, "[OK] Bink Audio: %u bytes written to %s\n", CompressedLen, OutputPath);
    return 0;
}
