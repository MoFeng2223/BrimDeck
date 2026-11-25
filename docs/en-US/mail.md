# Mailboxes

With a mailbox connected, BrimDeck sends a notice when new mail arrives:

- The notice shows the sender, the subject and the start of the text, on the island and on the notices page like any other notice.
- A verification code in the mail can be copied with the "Copy code" button (turn on Settings › Live notices › How notices show › Hide verification codes).
- "Open mailbox" opens the mailbox's website in the browser.

If you already use a mail app such as Outlook, Foxmail or Thunderbird, or have turned on notifications for webmail in the browser, you do not need to connect a mailbox: those Windows notifications already show on the notices page (see [Live notices](notices.md)). Connecting a mailbox is for when you would rather not keep a mail app running.

## Adding a mailbox

1. Open Settings › Live notices › Mailboxes and choose "Add mailbox".
2. Choose the mail service.
3. Follow the steps in the window to generate an authorization code (or app password) on the service's website, enter the address and the code, and choose "Connect".

BrimDeck signs in once with what you entered and saves the mailbox only after that worked. Up to 8 mailboxes can be added.

| Service | What to enter | Where to get it |
|---|---|---|
| QQ Mail (qq.com, foxmail.com) | Authorization code (授权码) | QQ Mail website, 设置 › 账号与安全: turn on the IMAP/SMTP service and generate a code |
| NetEase Mail (163.com, 126.com, yeah.net) | Authorization code (授权码) | NetEase Mail website, 设置 › POP3/SMTP/IMAP: turn on the IMAP/SMTP service |
| Tencent Exmail | The mailbox password; with 安全登录 on, a client password (客户端专用密码) | Exmail website, 设置 › 收发信设置 to turn on IMAP; the client password under 设置 › 邮箱绑定 |
| Sina Mail | Authorization code (授权码) | Sina Mail website, 设置 › 客户端pop/imap/smtp |
| iCloud Mail | App-specific password | account.apple.com, Sign-In and Security › App-Specific Passwords; needs two-factor authentication |
| Gmail | App password | myaccount.google.com/apppasswords; needs 2-Step Verification; work or school accounts may not offer it |
| Other mailbox | Password or authorization code, and the IMAP server | The mailbox's web settings or its administrator |

Outlook, Hotmail and Microsoft 365 mailboxes take neither a password nor a code; they connect by signing in to the Microsoft account:

1. In Outlook on the web, open Settings at the top right, go to Mail › Forwarding and IMAP and turn on Let devices and apps use IMAP. Microsoft says IMAP is off by default for personal accounts; skip this step if it is already on. For a work or school account, IMAP is controlled by its administrator.
2. In BrimDeck, choose Outlook, then Sign in in the browser.
3. On the Microsoft page that opens in the browser, sign in to the account to connect and accept the request. When the page says you are signed in, go back to BrimDeck. BrimDeck connects the mailbox of the account you signed in to on that page.

The consent page calls this permission read and write access to your mail. That is how Microsoft describes IMAP access, and there is no read-only version to ask for. BrimDeck opens the inbox read-only and does not change, move or delete mail.

BrimDeck has not been through Microsoft's publisher verification, so the sign-in page shows its publisher as unverified. If a work or school account asks for administrator approval, an administrator of that organization has to approve BrimDeck once. BrimDeck keeps only the sign-in credential Microsoft gives it, never the account password.

Only encrypted connections on port 993 (IMAP over TLS) are supported.

## The list of mailboxes

Each mailbox has a row with the state of its connection:

| State | Meaning |
|---|---|
| Connected | New mail is being received |
| Connecting… | Signing in |
| Connection lost; trying again shortly | The network or the server is unavailable for now. BrimDeck tries again after 5 seconds, 15 seconds, 1 minute, then every 5 minutes |
| Sign-in refused | The server refused the sign-in: the code no longer works (after a password change, for example) or IMAP was turned off. BrimDeck stops trying by itself, since repeated failures can get an account restricted; choose "Enter again" for a new code, or "Retry" |

Each row also lets you:

- choose the level of this mailbox's notices (default, level 1 to 3, or notices page only); see [Live notices](notices.md#notice-levels).
- pause it with its switch.
- remove the mailbox with the × at the right, which also deletes the code saved on this PC.

## When you are told

- Only unread mail that arrives after the connection is made. Mail already in the inbox when the mailbox is added, or when BrimDeck starts, is not announced.
- Mail that arrived while the PC slept or was offline is listed unread on the notices page once the connection is back, without popping up (this holds for any mail more than 10 minutes old when BrimDeck learns of it).
- Each mail takes its own row on the notices page; several mails from one sender are not put together.
- At most the latest 20 at a time.
- With both switches of live notices off, mailboxes are not connected.

Where the server pushes (IMAP IDLE), new mail is usually announced within seconds; BrimDeck also looks every 60 seconds, since some services do not always push.

## Privacy

- BrimDeck connects straight to the mailbox's own server; nothing goes through a relay.
- Only the sender, subject, time and the start of the text of new mail in the inbox are read (about 500 bytes of plain text; up to 16 KB of an HTML mail, from which the text is taken). Attachments are not downloaded and other folders are not read.
- The inbox is opened read-only: nothing is marked read, sent, moved or deleted.
- The code is stored with Windows' encryption for the current user (DPAPI) in `%LOCALAPPDATA%\BrimDeck\secrets.dat`; `settings.json` holds only the address and the service's name. An exported configuration does not contain the code, so it has to be entered again after importing on another PC.
- Mail notices go into the notice history like every other notice, encrypted on this PC; see [Live notices](notices.md#privacy) for how long they are kept.

IMAP is spoken with MailKit (MIT license; see ThirdPartyNotices.txt).
