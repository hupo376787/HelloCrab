# Pinterest adapter

The adapter consumes Pinterest browser Fetch/XHR responses only for profile/board pagination and Pin ID discovery. It does not construct or call the internal `PinResource` endpoint.

Supported pages:

- `https://www.pinterest.com/{username}/`
- `https://www.pinterest.com/{username}/_created/`
- `https://www.pinterest.com/{username}/{board-slug}/`

Recognized list resources include `UserPinsResource`, `ProfilePinsResource`, `BoardFeedResource`, `BoardSectionPinsResource`, and `UserActivityPinsResource`. Pagination is detected from `resource_response.bookmark` / `bookmarks` and continues by scrolling the real page.

Detail resolution:

1. The list response supplies the Pin ID.
2. The adapter fetches the public `https://www.pinterest.com/pin/{id}/` document with the current browser session.
3. It parses `script#__PWS_INITIAL_PROPS__`.
4. It first reads `initialReduxState.pins[id]`, then the embedded `resources.PinResource[*].data`, with a recursive fallback for future schema changes.
5. Video renditions are collected from every `video_list`, including `story_pin_data.pages[].blocks[].video.video_list`.

Media selection:

- Standard image Pins: largest actual `width × height`, preferring `orig` and larger named renditions.
- Video Pins: largest actual rendition from the detail document; equal-size candidates prefer direct MP4/MOV, then bitrate/file size.
- HLS `.m3u8` playlists and segments are downloaded by the application HTTP client (so the system proxy is honored), rewritten to a local playlist, and then remuxed locally to MP4 by FFmpeg. If the system HTTP path fails, the downloader retries through the Playwright browser-context request channel; direct FFmpeg networking remains only as a final compatibility fallback.
- Story/carousel Pins: each page/item is downloaded in order.
- Author name/avatar: `UserResource`, `BoardResource`, `pinner`, or `board.owner`; unrelated logged-in-user profile responses are ignored.
