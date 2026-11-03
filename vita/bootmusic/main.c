// DFU boot music: a taiHEN plugin under *DFUV00001 (tai/config.txt) that plays the title theme from process start, through the
// ~60 s Unity engine load. It streams ux0:data/DaggerfallUnity/bootmusic.raw (48 kHz stereo s16le, rendered by render.sh at the
// default MusicVolume 0.5) on its own audio port, scaled by settings.ini's MusicVolume, and fades out when the game creates
// UserData/bootmusic.stop (VitaBootMusic.cs: first video or the setup wizard), then removes the flag and ends its thread.
// Start times go to TTY and UserData/bootmusic.log.
#include <psp2/audioout.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/modulemgr.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>

#define DIR "ux0:data/DaggerfallUnity/"
#define MUSIC DIR "bootmusic.raw"
#define STOP_FLAG DIR "UserData/bootmusic.stop"
#define SETTINGS DIR "UserData/settings.ini"
#define LOG DIR "UserData/bootmusic.log"

// 85 ms per output call: the hardware queues about two, so the thread may run ~85 ms late before a gap. With 21 ms grains it
// crackled all through the engine load even with the data in memory (session 14): Unity's start-up threads keep the CPU.
#define GRAIN 4096                 // frames per output call (85.3 ms)
#define FADE_GRAINS 12             // ~1 s fade-out

// Two output buffers, alternating: sceAudioOutOutput returns while the hardware still reads the buffer it was given (it only waits
// for the previous one), so refilling a single buffer tore the sound that was playing (the "scratchy" boot music, session 14).
static short buffers[2][GRAIN * 2];

static void log_line(const char *line, int len)
{
    sceClibPrintf("%s", line);
    SceUID fd = sceIoOpen(LOG, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
    if (fd >= 0)
    {
        sceIoWrite(fd, line, len);
        sceIoClose(fd);
    }
}

#define LOGF(...) do { char l[192]; int n = sceClibSnprintf(l, sizeof(l), __VA_ARGS__); log_line(l, n); } while (0)

static int exists(const char *path)
{
    SceIoStat st;
    return sceIoGetstat(path, &st) >= 0;
}

// MusicVolume from settings.ini, in 1/1000 (default 500 = what the file was rendered at)
static int music_volume(void)
{
    static char ini[16384];
    SceUID fd = sceIoOpen(SETTINGS, SCE_O_RDONLY, 0);
    if (fd < 0)
        return 500;
    int n = sceIoRead(fd, ini, sizeof(ini) - 1);
    sceIoClose(fd);
    if (n <= 0)
        return 500;
    ini[n] = 0;
    const char *key = "MusicVolume";
    for (char *p = ini; *p; p++)
    {
        if ((p == ini || p[-1] == '\n') && sceClibStrncmp(p, key, 11) == 0)
        {
            p += 11;
            while (*p == ' ' || *p == '=')
                p++;
            int whole = 0, frac = 0, scale = 1000;
            while (*p >= '0' && *p <= '9')
                whole = whole * 10 + (*p++ - '0');
            if (*p == '.')
                for (p++; *p >= '0' && *p <= '9' && scale > 1; p++)
                    frac = frac * 10 + (*p - '0'), scale /= 10;
            int v = whole * 1000 + frac * scale;
            return v > 1000 ? 1000 : v;
        }
    }
    return 500;
}

// Read-ahead ring: Unity's loading keeps the memory card busy, so small reads per buffer can stall behind it
// (session 14). A reader thread fills 256 KB chunks; playback only copies from memory. Allocated for the music, freed after.
#define CHUNK (256 * 1024)
#define CHUNKS 8                   // 2 MB = ~11 s of audio
static char *ring;
static volatile int filled[CHUNKS];  // bytes in the chunk; 0 = free for the reader, -1 = end of file
static volatile int quit;
// The audio thread does no file access: a stat or log write queued behind Unity's big reads froze it for up to 2.3 s (session 14,
// run 72: late gaps of 804 ms and 2264 ms with the data already in memory). The reader thread checks the stop flag and writes the
// log; the audio thread only reads these.
static volatile int stop_requested, late, worst, underruns;

static int reader_thread(SceSize args, void *argp)
{
    SceUID fd = *(SceUID *)argp;
    SceUInt32 report = sceKernelGetProcessTimeLow();   // 32-bit µs: no 64-bit division without a libc
    int c = 0, eof = 0;
    while (!quit)
    {
        if (!eof && filled[c] == 0)
        {
            int got = sceIoRead(fd, ring + c * CHUNK, CHUNK);
            __sync_synchronize();          // data before the count
            filled[c] = got > 0 ? got : -1;
            eof = got <= 0;
            c = (c + 1) % CHUNKS;
            continue;
        }
        sceKernelDelayThread(100000);
        if (!stop_requested && exists(STOP_FLAG))
        {
            stop_requested = 1;
            LOGF("DFUBOOT stop flag, fading out, process_us=%llu\n", (unsigned long long)sceKernelGetProcessTimeWide());
        }
        SceUInt32 now = sceKernelGetProcessTimeLow();
        if (now - report > 5000000)
        {
            LOGF("DFUBOOT t=%us late=%d worst_gap=%dms underrun_waits=%d\n", now / 1000000, late, worst / 1000, underruns);
            report = now; late = worst = 0;
        }
    }
    return 0;
}

static int music_thread(SceSize args, void *argp)
{
    SceUID fd = sceIoOpen(MUSIC, SCE_O_RDONLY, 0);
    if (fd < 0)
    {
        LOGF("DFUBOOT no %s (0x%08x)\n", MUSIC, fd);
        return 0;
    }
    SceUID block = sceKernelAllocMemBlock("DFUBootMusicRing", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, CHUNK * CHUNKS, NULL);
    if (block < 0 || sceKernelGetMemBlockBase(block, (void **)&ring) < 0)
    {
        LOGF("DFUBOOT ring alloc failed 0x%08x\n", block);
        sceIoClose(fd);
        return 0;
    }
    SceUID reader = sceKernelCreateThread("DFUBootMusicRead", reader_thread, 130, 0x2000, 0, 0, NULL);
    sceKernelStartThread(reader, sizeof(fd), &fd);

    int volume = music_volume();
    int port = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_MAIN, GRAIN, 48000, SCE_AUDIO_OUT_MODE_STEREO);
    if (port < 0)
        LOGF("DFUBOOT audio port failed 0x%08x\n", port);
    else
        LOGF("DFUBOOT playing, MusicVolume %d/1000, process_us=%llu\n", volume, (unsigned long long)sceKernelGetProcessTimeWide());

    // gain in 1/1024: rendered at 0.5, so MusicVolume 0.5 = 1024
    int gain = volume * 2048 / 1000, fade = -1, c = 0, off = 0, next = 0;
    SceUInt32 last = 0;
    while (port >= 0 && fade != 0)
    {
        while (filled[c] == 0)
        {
            underruns++;
            sceKernelDelayThread(2000);
        }
        if (filled[c] < 0)
            break;          // end of the file (render.sh fades its last seconds)
        __sync_synchronize();          // count before the data
        short *buffer = buffers[next];
        next ^= 1;
        int got = filled[c] - off < (int)sizeof(buffers[0]) ? filled[c] - off : (int)sizeof(buffers[0]);
        sceClibMemcpy(buffer, ring + c * CHUNK + off, got);
        if (got < (int)sizeof(buffers[0]))
            sceClibMemset((char *)buffer + got, 0, sizeof(buffers[0]) - got);
        if ((off += got) >= filled[c])
        {
            off = 0;
            __sync_synchronize();
            filled[c] = 0;
            c = (c + 1) % CHUNKS;
        }
        if (fade < 0 && stop_requested)
            fade = FADE_GRAINS;
        int g = fade < 0 ? gain : gain * fade-- / FADE_GRAINS;
        for (int i = 0; i < GRAIN * 2; i++)
        {
            int v = buffer[i] * g / 1024;
            buffer[i] = v > 32767 ? 32767 : v < -32768 ? -32768 : v;
        }
        sceAudioOutOutput(port, buffer);
        // How late the thread got: a gap between returns well over one grain means the output ran dry (crackle)
        SceUInt32 now = sceKernelGetProcessTimeLow();
        int gap = last ? (int)(now - last) : 0;
        last = now;
        if (gap > worst)
            worst = gap;
        if (gap > GRAIN * 1000 / 48 * 3 / 2)
            late++;
    }
    if (port >= 0)
    {
        sceAudioOutOutput(port, NULL);   // wait until the last buffer has played
        sceAudioOutReleasePort(port);
    }
    quit = 1;
    sceKernelWaitThreadEnd(reader, NULL, NULL);
    sceKernelDeleteThread(reader);
    sceKernelFreeMemBlock(block);
    sceIoClose(fd);
    sceIoRemove(STOP_FLAG);
    LOGF("DFUBOOT done, %d underrun waits, process_us=%llu\n", underruns, (unsigned long long)sceKernelGetProcessTimeWide());
    return 0;
}

void _start() __attribute__((weak, alias("module_start")));
int module_start(SceSize args, void *argp)
{
    LOGF("DFUBOOT module_start process_us=%llu system_us=%llu\n",
        (unsigned long long)sceKernelGetProcessTimeWide(), (unsigned long long)sceKernelGetSystemTimeWide());
    sceIoRemove(STOP_FLAG);   // left over from a crash or a quit before the first video
    // Audio: highest user priority band, which must be pinned to one core (64-127)
    SceUID thread = sceKernelCreateThread("DFUBootMusic", music_thread, 64, 0x4000, 0, SCE_KERNEL_CPU_MASK_USER_2, NULL);
    if (thread >= 0)
        sceKernelStartThread(thread, 0, NULL);
    else
        LOGF("DFUBOOT thread failed 0x%08x\n", thread);
    return SCE_KERNEL_START_SUCCESS;
}

int module_stop(SceSize args, void *argp)
{
    return SCE_KERNEL_STOP_SUCCESS;
}
