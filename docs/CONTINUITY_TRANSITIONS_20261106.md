# Continuous playback and music transitions

The operator requested unattended recovery, a music-to-music crossfade on every station except Classic, and the 10 November special programme. The existing public service must not be treated as a test fixture. A new build must pass isolated tests before an installation or station restart is considered.

## Audio references

- [ITU-R BS.1770-5](https://www.itu.int/rec/R-REC-BS.1770-5-202311-I/en) specifies programme-loudness and true-peak measurement algorithms.
- [EBU R 128](https://tech.ebu.ch/publications/r128) specifies loudness normalisation, loudness range and maximum true-peak descriptors; its programme target is −23 LUFS. [EBU R 128 s3](https://tech.ebu.ch/publications/r128s3) and [Tech 3401](https://tech.ebu.ch/publications/tech3401) address radio distribution and streaming.
- The currently installed source normalization uses −23 LUFS, bounded boost and source peak headroom. Meter readings, source estimates, final-mix measurements and independently decoded listener output are distinct. Do not represent a target or limiter as proof of codec-output compliance without measurement.

## Transition implementation gates

The separate equal-power mixer is a building block only; it does not currently alter the playing stream. A candidate implementation must:

1. Crossfade consecutive eligible music items on all configured channels except station 1 (Classic). Preserve the normal jingle and sweeper rules, and avoid overlapping a music fade with a branding item or a scheduled timed insertion.
2. Prepare the next decoder and fade coefficients outside the programme timing lane. Do not wait for a new decoder, filesystem access, metadata networking or retirement of an old decoder during real-time mixing. If the incoming decoder is unavailable, continue the outgoing source and use a bounded, observable fallback.
3. Preserve the ordinary queue and rotation ownership, exact sample-based event boundaries, restart semantics and metadata history. Overlapping audio must not cause two outputs of the same station to count as two plays.
4. Apply the normal final-mix true-peak limiter after the overlap and validate independently decoded AAC-LC, HE-AACv2 and Ogg FLAC outputs. Include correlated material, silence, short items, broken or slow decoders, uneven read chunks, queue edits and stop/restart during a fade. A fixed 3-second equal-power transition is an initial engineering choice to test, not an EBU/ITU-mandated crossfade duration.
5. Prove the Classic stream is unchanged and check natural, content-specific transitions. Use realistic load and report interruptions rather than treating connected TCP sockets as proof of uninterrupted listening.

## Autonomy and special-day gates

The installed Windows service is configured for automatic startup and restart after service failures. Output reconnection is independently supervised. These settings do not guarantee service access to removable disks at boot, server acceptance, zero listener reconnects or decoded audio after a network outage; each requires an actual test.

The 10 November 2026 request targets station 4 (/radio and /radio-low): an all-day shuffled list without station jingles/sweepers; the supplied recording at 09:05 Istanbul; fixed text `Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz`; and a return to the normal queue at 11 November 00:00. The 35 prepared files were decoded in isolation and read by a running service-account source check. They are **not scheduled**. Draft suppression, date selection, shuffle, and insertion-attempt records are incomplete building blocks: publication, a durable validated media snapshot, exact-time preemption, full recording playback, metadata override, normal-queue preservation, restart recovery, UI activation and real-audio qualification remain required.

On 6 November the primary /radio source was unavailable for hours while other stations continued. All outputs recovered around 21:09 Istanbul; simultaneous listener resets and temporary HTTP errors were observed. The server-side cause remains unproven and no server administrative access is available. This incident prevents a zero-interruption claim.
