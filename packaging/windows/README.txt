APPLICATION TRACKER
===================

A simple, private tracker for job applications: a board and a list of where each
application stands, follow-up reminders, contacts and notes, your CV and cover-letter
versions, and a stats page. It runs on this computer only; there is no account and
nothing is sent anywhere.


STARTING AND STOPPING
---------------------
- Still looking inside the zip? Close it, right-click the zip, choose "Extract All",
  and use the extracted folder. The app can't run from inside the zip.
- Double-click "Start ApplicationTracker.cmd". A black window opens, and after a few
  seconds your web browser opens at http://localhost:5180. Bookmark it if you like;
  it only works while the black window is open.
- Keep the black window open while you use it. Close it to stop Application Tracker.
- Want to look around first? Double-click "Try the demo.cmd" instead. It shows made-up
  applications at http://localhost:5181 and never touches your own data.

The first time, Windows may say "Windows protected your PC" because this app isn't
signed with a (paid) certificate. Click "More info", then "Run anyway".


WHERE YOUR DATA IS, AND BACKING IT UP
-------------------------------------
Everything is kept in this folder (paste it into the File Explorer address bar):

    %LOCALAPPDATA%\ApplicationTracker\data

- tracker.db holds your applications, notes, contacts and reminders.
- tracker-documents holds the CVs and cover letters you upload.
- demo.db and demo-documents are the demo's made-up data; you can delete them.

To back up, close Application Tracker and copy the whole
%LOCALAPPDATA%\ApplicationTracker folder somewhere safe (a USB stick or cloud drive).
To restore a backup, close Application Tracker and copy the folder back.

To update to a newer version, close Application Tracker, delete this folder (the one
with the .cmd files) and extract the new version. Your data stays where it is.
The version you have is shown at the bottom of the app's menu.

To remove Application Tracker, delete this folder. To delete your data as well,
delete the %LOCALAPPDATA%\ApplicationTracker folder too.


GETTING FEEDBACK ON YOUR CV
---------------------------
1. On the Documents page, upload your CV.
2. Open an application, click "Edit details" and paste the job ad into
   "Job description". Then, back on the application, link your CV under
   "Documents used".
3. Click "Get feedback" and follow the steps: download your CV, open a chat assistant
   such as claude.ai, attach the CV, then copy and paste the ready-made prompt.

The app never sends your CV anywhere itself; you choose what to paste or attach.


IF SOMETHING GOES WRONG
-----------------------
- "Address already in use": Application Tracker is probably already running in another
  black window. Use that one, or close it and start again.
- The browser didn't open: open http://localhost:5180 yourself.
- Anything else: https://github.com/cckraken/ApplicationTracker/issues


Licence: MIT (see LICENSE). Third-party components: see THIRD-PARTY-NOTICES.txt.
