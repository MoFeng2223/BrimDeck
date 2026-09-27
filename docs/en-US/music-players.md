# Supported players

The music page and the collapsed view show the song that is playing and offer playback controls. What is available depends on the player:

| Player | Shown | Controls |
| --- | --- | --- |
| NetEase Cloud Music | Track, cover, progress | With SMTC on: play, pause, previous and next<br>With full control: also seeking and playback modes (shuffle, repeat all, repeat one) |
| QQ Music | Track, progress | Play, pause, previous, next and seeking |
| Other players | The track, cover and progress the player shares with Windows media controls | The play, pause, previous, next and seeking the player supports |

"Other players" are players that use Windows media controls, that is, players that appear in the media card of the Windows volume flyout while they play.

## NetEase Cloud Music

- **By default**: the track, cover and progress are shown without any setup. The progress is an estimate.
- **With SMTC on**: in NetEase Cloud Music, open 设置 › 系统 and turn on 开启 SMTC (the NetEase client is in Chinese). You can then play, pause and change tracks from BrimDeck.
- **Full control**: select Enable full control on the music page and confirm; NetEase Cloud Music restarts. It then shows the actual progress, and you can seek and switch playback modes (shuffle, repeat all, repeat one). Starting NetEase Cloud Music the normal way returns it to normal mode.

Full control needs NetEase Cloud Music to open a local debugging port, which carries some risk while it is open. Read [Data and privacy](privacy.md#netease-cloud-music-full-control) before you turn it on.

To keep full control available at any time, turn on Always show "Enable full control" in Settings › Music › Players.
