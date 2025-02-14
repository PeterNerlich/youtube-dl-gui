﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace youtube_dl_gui {
    public partial class frmDownloader : Form {
        Language lang = Language.GetInstance();
        Verification verif = Verification.GetInstance();

        public DownloadInfo CurrentDownload;
        public bool Debugging = false;

        private Process DownloadProcess;        // The process of youtube-dl which we'll redirect.
        private Thread DownloadThread;          // The thread of the process for youtube-dl.
        private bool AbortBatch = false;        // Determines if the rest of the batch downloads should be cancelled.

        public frmDownloader() {
            InitializeComponent();
        }
        private void frmDownloader_Load(object sender, EventArgs e) {
            //CurrentDownload.BatchDownload = true;
            this.Text = lang.frmDownloader + " ";
            chkDownloaderCloseAfterDownload.Text = lang.chkDownloaderCloseAfterDownload;
            if (CurrentDownload.BatchDownload) {
                this.WindowState = FormWindowState.Minimized;
                btnDownloaderAbortBatchDownload.Enabled = true;
                btnDownloaderAbortBatchDownload.Visible = true;
                btnDownloaderAbortBatchDownload.Text = lang.btnDownloaderAbortBatch;
                btnDownloaderCancelExit.Text = lang.GenericSkip;
            }
            else {
                btnDownloaderCancelExit.Text = lang.GenericCancel;
            }
            chkDownloaderCloseAfterDownload.Checked = Downloads.Default.CloseDownloaderAfterFinish;
        }
        private void frmDownloader_Shown(object sender, EventArgs e) {
            BeginDownload();
        }
        private void frmDownloader_FormClosing(object sender, FormClosingEventArgs e) {
            DialogResult Finish = DialogResult.None;
            switch (CurrentDownload.Status) {
                case DownloadStatus.Aborted:
                    if (CurrentDownload.BatchDownload) {
                        if (AbortBatch) {
                            Finish = DialogResult.Abort;
                        }
                        else {
                            Finish = DialogResult.Ignore;
                        }
                    }
                    else { this.Dispose(); }
                    break;
                case DownloadStatus.Finished:
                    if (DownloadProcess.ExitCode == 0) {
                        if (CurrentDownload.BatchDownload) { this.DialogResult = System.Windows.Forms.DialogResult.Yes; }
                        else { this.Dispose(); }
                    }
                    else {
                        if (CurrentDownload.BatchDownload) { this.DialogResult = System.Windows.Forms.DialogResult.No; }
                    }
                    break;
                case DownloadStatus.ProgramError:
                case DownloadStatus.YtdlError:
                    if (CurrentDownload.BatchDownload) { Finish = DialogResult.No; }
                    else { this.Dispose(); }
                    break;
                default:
                    if (DownloadThread != null && DownloadThread.IsAlive) {
                        if (DownloadProcess != null && !DownloadProcess.HasExited) {
                            DownloadProcess.Kill();
                        }
                        DownloadThread.Abort();
                        CurrentDownload.Status = DownloadStatus.Aborted;
                        e.Cancel = true;
                    }
                    break;
            }

            if (!e.Cancel) {
                if (Downloads.Default.CloseDownloaderAfterFinish != chkDownloaderCloseAfterDownload.Checked && !CurrentDownload.BatchDownload) {
                    Downloads.Default.CloseDownloaderAfterFinish = chkDownloaderCloseAfterDownload.Checked;
                    if (!Program.IsPortable) {
                        Downloads.Default.Save();
                    }
                }

                this.DialogResult = Finish;
                this.Dispose();
            }
        }
        private void btnDownloaderAbortBatchDownload_Click(object sender, EventArgs e) {
            switch (CurrentDownload.Status) {
                case DownloadStatus.YtdlError: case DownloadStatus.ProgramError: case DownloadStatus.Aborted:
                    btnDownloaderAbortBatchDownload.Visible = false;
                    btnDownloaderAbortBatchDownload.Enabled = false;
                    BeginDownload();
                    break;
                default:
                    AbortBatch = true;
                    switch (CurrentDownload.Status) {
                        case DownloadStatus.Finished: case DownloadStatus.Aborted:
                        case DownloadStatus.YtdlError: case DownloadStatus.ProgramError:
                            rtbConsoleOutput.AppendText("The user requested to abort subsequent batch downloads");
                            btnDownloaderAbortBatchDownload.Enabled = false;
                            break;
                        default:
                            CurrentDownload.Status = DownloadStatus.Aborted;
                            if (DownloadThread != null && DownloadThread.IsAlive) {
                                DownloadThread.Abort();
                            }
                            System.Media.SystemSounds.Hand.Play();
                            rtbConsoleOutput.AppendText("Downloading was aborted by the user.\n");
                            rtbConsoleOutput.AppendText("Additionally, the batch download has been canceled.");
                            this.Close();
                            break;
                    }
                    break;
            }
        }
        private void btnDownloaderCancelExit_Click(object sender, EventArgs e) {
            switch (CurrentDownload.Status) {
                case DownloadStatus.Finished:
                case DownloadStatus.YtdlError:
                case DownloadStatus.Aborted:
                    this.Close();
                    break;
                default:
                    if (DownloadThread != null && DownloadThread.IsAlive) {
                        DownloadThread.Abort();
                    }
                    break;
            }
        }

        private void BeginDownload() {
            Debug.Print("BeginDownload()");
            if (string.IsNullOrEmpty(CurrentDownload.DownloadURL)) {
                MessageBox.Show("The URL is null or empty. Please enter a URL or Download path.");
                return;
            }
            CurrentDownload.Status = DownloadStatus.GeneratingArguments;
            rtbConsoleOutput.AppendText("Beginning download, this box will output progress\n");
            if (CurrentDownload.BatchDownload) { chkDownloaderCloseAfterDownload.Checked = true; }

            #region URL cleaning
            if (!CurrentDownload.DownloadURL.StartsWith("https://")) {
sanitizecheck:
                if (CurrentDownload.DownloadURL.StartsWith("\\'")|| // single quote
                CurrentDownload.DownloadURL.StartsWith("\\\"")   || // double quote
                CurrentDownload.DownloadURL.StartsWith("\\n")    || // newline
                CurrentDownload.DownloadURL.StartsWith("\\r")    || // carriage-return
                CurrentDownload.DownloadURL.StartsWith("\\t")    || // tab
                CurrentDownload.DownloadURL.StartsWith("\\0")    || // null char
                CurrentDownload.DownloadURL.StartsWith("\\b")    || // backspace
                CurrentDownload.DownloadURL.StartsWith("\\")) {     // backslash
                    CurrentDownload.DownloadURL = CurrentDownload.DownloadURL.Substring(4);
                    goto sanitizecheck;
                }

                if (CurrentDownload.DownloadURL.StartsWith("http://")) {
                    CurrentDownload.DownloadURL = "https" + CurrentDownload.DownloadURL.Substring(4);
                }
            }
            #endregion

            string YoutubeDlFileName = null;
            string ArgumentsBuffer = string.Empty;
            string PreviewArguments = string.Empty;
            string QualityFormatBuffer = string.Empty;
            string hlsFF = string.Empty;
            string webFolder = string.Empty;

            #region youtube-dl path
            if (General.Default.UseStaticYtdl && File.Exists(General.Default.ytdlPath)) {
                YoutubeDlFileName = General.Default.ytdlPath;
            }
            else {
                YoutubeDlFileName = verif.YoutubeDlPath;
            }
            if (YoutubeDlFileName == null) {
                rtbConsoleOutput.AppendText("Youtube-DL has not been found\nA rescan for youtube-dl was called\n");
                verif.RefreshYoutubeDlLocation();
                if (verif.YoutubeDlPath != null) {
                    rtbConsoleOutput.AppendText("Rescan finished and found, continuing\n");
                }
                else {
                    rtbConsoleOutput.AppendText("still couldnt find youtube-dl.");
                    CurrentDownload.Status = DownloadStatus.ProgramError;
                    return;
                }
            }
            rtbConsoleOutput.AppendText("Youtube-DL has been found and set\n");
            #endregion

            #region Output
            rtbConsoleOutput.AppendText("Generating output directory structure\n");

            if (Downloads.Default.separateIntoWebsiteURL) {
                webFolder = Download.getUrlBase(CurrentDownload.DownloadURL) + "\\";
            }

            string OutputDirectory = "\"" + Downloads.Default.downloadPath;
            if (CurrentDownload.BatchDownload && Downloads.Default.SeparateBatchDownloads) {
                OutputDirectory += "\\# Batch Downloads #";
                if (Downloads.Default.AddDateToBatchDownloadFolders) {
                    OutputDirectory += "\\" + CurrentDownload.BatchTime;
                }
            }
            OutputDirectory += "\\" + webFolder + "{0}" + Downloads.Default.fileNameSchema + "\"";

            ArgumentsBuffer = CurrentDownload.DownloadURL + " -o " + OutputDirectory;

            if (Downloads.Default.separateDownloads) {
                switch (CurrentDownload.Type) {
                    case DownloadType.Video:
                        ArgumentsBuffer = ArgumentsBuffer.Replace("{0}", "Video\\");
                        break;
                    case DownloadType.Audio:
                        ArgumentsBuffer = ArgumentsBuffer.Replace("{0}", "Audio\\");
                        break;
                    case DownloadType.Custom:
                        ArgumentsBuffer = ArgumentsBuffer.Replace("{0}", "Custom\\");
                        break;
                    default:
                        rtbConsoleOutput.AppendText("Unable to determine what download type to use (expected 0, 1, or 2)");
                        CurrentDownload.Status = DownloadStatus.ProgramError;
                        return;
                }
            }
            else {
                ArgumentsBuffer = ArgumentsBuffer.Replace("{0}", "");
            }
            rtbConsoleOutput.AppendText("The output was generated and will be used\n");
            #endregion

            #region Quality + format
            switch (CurrentDownload.Type) {
                case DownloadType.Video: {
                    if (CurrentDownload.SkipAudioForVideos) {
                        ArgumentsBuffer += DownloadFormats.GetVideoQualityArgsNoSound(CurrentDownload.VideoQuality);
                    }
                    else {
                        ArgumentsBuffer += DownloadFormats.GetVideoQualityArgs(CurrentDownload.VideoQuality);
                    }
                        
                    ArgumentsBuffer += DownloadFormats.GetVideoRecodeInfo(CurrentDownload.VideoFormat);
                        break;
                }
                case DownloadType.Audio: {
                        if (CurrentDownload.AudioCBRQuality == AudioCBRQualityType.best || CurrentDownload.AudioVBRQuality == AudioVBRQualityType.q0) {
                            ArgumentsBuffer += " -f  -x --audio-format best --audio-quality 0";
                        }
                        else {
                            if (CurrentDownload.UseVBR) {
                                if (CurrentDownload.AudioVBRQuality == AudioVBRQualityType.q0) {
                                    ArgumentsBuffer += " -f bestaudio --extract-audio --audio-quality 0";
                                }
                                else {
                                    ArgumentsBuffer += " --extract-audio --audio-quality " + CurrentDownload.AudioVBRQuality;
                                }
                            }
                            else {
                                if (CurrentDownload.AudioCBRQuality == AudioCBRQualityType.best) {
                                    ArgumentsBuffer += " -f bestaudio --extract-audio --audio-quality 0";
                                }
                                else {
                                    ArgumentsBuffer += " --extract-audio --audio-quality " + DownloadFormats.GetAudioQuality(CurrentDownload.AudioCBRQuality);
                                }
                            }
                        }
                        break;
                    }
                case DownloadType.Custom: {
                        rtbConsoleOutput.AppendText("Custom was requested, skipping quality + format");
                        ArgumentsBuffer += " " + CurrentDownload.DownloadArguments;
                        break;
                    }
                default: {
                        rtbConsoleOutput.AppendText("Expected a downloadtype (Quality + Format)");
                        CurrentDownload.Status = DownloadStatus.ProgramError;
                        return;
                    }
            }

            rtbConsoleOutput.AppendText("The quality and format has been set\n");
            #endregion

            #region Arguments
            if (CurrentDownload.Type != DownloadType.Custom) {
                switch (CurrentDownload.PlaylistSelection) {
                    case PlaylistSelectionType.PlaylistStartPlaylistEnd: // playlist-start and playlist-end
                        if (CurrentDownload.PlaylistSelectionIndexStart > 0) {
                            ArgumentsBuffer += " --playlist-start " + CurrentDownload.PlaylistSelectionIndexStart;
                        }

                        if (CurrentDownload.PlaylistSelectionIndexEnd > 0) {
                            ArgumentsBuffer += " --playlist-end " + (CurrentDownload.PlaylistSelectionIndexStart + CurrentDownload.PlaylistSelectionIndexEnd);
                        }
                        break;
                    case PlaylistSelectionType.PlaylistItems: // playlist-items
                        ArgumentsBuffer += " --playlist-items " + CurrentDownload.PlaylistSelectionArg;
                        break;
                    case PlaylistSelectionType.DateBefore: // datebefore
                        ArgumentsBuffer += " --datebefore " + CurrentDownload.PlaylistSelectionArg;
                        break;
                    case PlaylistSelectionType.DateDuring: // date
                        ArgumentsBuffer += " --date " + CurrentDownload.PlaylistSelectionArg;
                        break;
                    case PlaylistSelectionType.DateAfter: // dateafter
                        ArgumentsBuffer += " --dateafter " + CurrentDownload.PlaylistSelectionArg;
                        break;
                }

                if (Downloads.Default.PreferFFmpeg || Download.isReddit(CurrentDownload.DownloadURL) && Downloads.Default.fixReddit) {
                    rtbConsoleOutput.AppendText("Looking for ffmpeg\n");
                    if (verif.FFmpegPath != null) {
                        if (General.Default.UseStaticFFmpeg) {
                            ArgumentsBuffer += " --ffmpeg-location \"" + General.Default.ffmpegPath + "\\ffmpeg.exe\"";
                        }
                        else {
                            ArgumentsBuffer += " --ffmpeg-location \"" + verif.FFmpegPath + "\\ffmpeg.exe\" --hls-prefer-ffmpeg";
                        }
                        rtbConsoleOutput.AppendText("ffmpeg was found\n");
                    }
                    else {
                        rtbConsoleOutput.AppendText("ffmpeg path is null, downloading may be affected\n");
                    }
                }
                
                if (Downloads.Default.SaveSubtitles) {
                    ArgumentsBuffer += " --all-subs";
                    if (!string.IsNullOrEmpty(Downloads.Default.SubtitleFormat)) {
                        ArgumentsBuffer += " --sub-format " + Downloads.Default.SubtitleFormat + " ";
                    }
                    if (Downloads.Default.EmbedSubtitles && CurrentDownload.Type == DownloadType.Video) {
                        switch (CurrentDownload.VideoFormat) {
                            case VideoFormatType.flv: case VideoFormatType.mkv:
                                break;
                        }
                        ArgumentsBuffer += " --embed-subs";
                    }
                }
                if (Downloads.Default.SaveVideoInfo) {
                    ArgumentsBuffer += " --write-info-json";
                }
                if (Downloads.Default.SaveDescription) {
                    ArgumentsBuffer += " --write-description";
                }
                if (Downloads.Default.SaveAnnotations) {
                    ArgumentsBuffer += " --write-annotations";
                }
                if (Downloads.Default.SaveThumbnail) {
                    // ArgumentsBuffer += "--write-all-thumbnails "; // Maybe?
                    //ArgumentsBuffer += " --write-thumbnail";
                    if (Downloads.Default.EmbedThumbnails) {
                        switch (CurrentDownload.Type) {
                            case DownloadType.Video:
                                if (CurrentDownload.VideoFormat == VideoFormatType.mp4) {
                                    ArgumentsBuffer += " --embed-thumbnail";
                                }
                                else {
                                    rtbConsoleOutput.AppendText("!!!!!!!! WARNING !!!!!!!!\nCannot embed thumbnail to non-mp4 videos files\n");
                                }
                                break;
                            case DownloadType.Audio:
                                if (CurrentDownload.AudioFormat == AudioFormatType.m4a || CurrentDownload.AudioFormat == AudioFormatType.mp3) {
                                    ArgumentsBuffer += " --embed-thumbnail";
                                }
                                else {
                                    rtbConsoleOutput.AppendText("!!!!!!!! WARNING !!!!!!!!\nCannot embed thumbnail to non-m4a/mp3 audio files\n");
                                }
                                break;
                        }
                    }
                    //if (Downloads.Default.EmbedThumbnails) {
                    //    switch (DownloadType) {
                    //        case 0:
                    //            if (DownloadFormat != 4) {
                    //                rtbConsoleOutput.AppendText("!!!!!!!! WARNING !!!!!!!!\nCannot embed thumbnail to non-mp4 videos files\nWill try anyway.\n");
                    //            }
                    //            break;
                    //        case 1:
                    //            if (DownloadFormat != 3 && DownloadFormat != 4) {
                    //                rtbConsoleOutput.AppendText("!!!!!!!! WARNING !!!!!!!!\nCannot embed thumbnail to non-m4a/mp3 audio files\nWill try anyway.\n");
                    //            }
                    //            break;
                    //    }
                    //    ArgumentsBuffer += " --embed-thumbnail";
                    //}
                }
                if (Downloads.Default.WriteMetadata) {
                    ArgumentsBuffer += " --add-metadata";
                }

                if (Downloads.Default.KeepOriginalFiles) {
                    ArgumentsBuffer += " -k";
                }

                if (Downloads.Default.LimitDownloads && Downloads.Default.DownloadLimit > 0) {
                    ArgumentsBuffer += " --limit-rate " + Downloads.Default.DownloadLimit;
                    switch (Downloads.Default.DownloadLimitType) {
                        case 0: { // kb
                                ArgumentsBuffer += "K ";
                                break;
                            }
                        case 1: { // mb
                                ArgumentsBuffer += "M ";
                                break;
                            }
                        case 2: { // gb
                                ArgumentsBuffer += "G ";
                                break;
                            }
                        default: { // kb default
                                ArgumentsBuffer += "K ";
                                break;
                            }
                    }
                }

                if (Downloads.Default.RetryAttempts != 10 && Downloads.Default.RetryAttempts > 0) {
                    ArgumentsBuffer += " --retries " + Downloads.Default.RetryAttempts;
                }

                if (Downloads.Default.ForceIPv4) {
                    ArgumentsBuffer += " --force-ipv4";
                }
                else if (Downloads.Default.ForceIPv6) {
                    ArgumentsBuffer += " --force-ipv6";
                }

                if (Downloads.Default.UseProxy && Downloads.Default.ProxyType > -1 && !string.IsNullOrEmpty(Downloads.Default.ProxyIP) && !string.IsNullOrEmpty(Downloads.Default.ProxyPort)) {
                    ArgumentsBuffer += " --proxy " + Download.ProxyProtocols[Downloads.Default.ProxyType] + Downloads.Default.ProxyIP + ":" + Downloads.Default.ProxyPort + "/ ";
                }
            }
            #endregion

            #region Authentication
            // Set the preview arguments to what is present in the arguments buffer.
            // This is so the arguments buffer can have sensitive information and
            // the preview arguments won't include it in case anyone creates an issue.
            PreviewArguments = ArgumentsBuffer;

            if (CurrentDownload.AuthUsername != null) {
                ArgumentsBuffer += " --username " + CurrentDownload.AuthUsername;
                CurrentDownload.AuthUsername = null;
                PreviewArguments += " --username ***";
            }
            if (CurrentDownload.AuthPassword != null) {
                ArgumentsBuffer += " --password " + CurrentDownload.AuthPassword;
                CurrentDownload.AuthPassword = null;
                PreviewArguments += " --password ***";
            }
            if (CurrentDownload.Auth2Factor != null) {
                ArgumentsBuffer += " --twofactor " + CurrentDownload.Auth2Factor;
                CurrentDownload.Auth2Factor = null;
                PreviewArguments += " --twofactor ***";
            }
            if (CurrentDownload.AuthVideoPassword != null) {
                ArgumentsBuffer += " --video-password " + CurrentDownload.AuthVideoPassword;
                CurrentDownload.AuthVideoPassword = null;
                PreviewArguments += " --video-password ***";
            }
            if (CurrentDownload.AuthNetrc) {
                CurrentDownload.AuthNetrc = false;
                ArgumentsBuffer += " --netrc";
                PreviewArguments += " --netrc";
            }
            #endregion

            rtbConsoleOutput.AppendText("Arguments have been generated and are readonly in the textbox\n");
            txtArgumentsGenerated.Text = PreviewArguments;

            #region Download thread
            //if (Program.IsDebug) {
            //    rtbConsoleOutput.Text = "===ARGUMENTS===\n" + ArgumentsBuffer.Replace(' ', '\n') + "\n\n===PREVIEW ARGUMENTS===\n" + PreviewArguments.Replace(' ', '\n');
            //    return;
            //}
            rtbConsoleOutput.AppendText("Creating download thread\n");
            DownloadThread = new Thread(() => {
                try {
                    while (true) {
                        Debug.Print("Sleeping");
                        Thread.Sleep(2000);
                    }
                    DownloadProcess = new Process() {
                        StartInfo = new System.Diagnostics.ProcessStartInfo(YoutubeDlFileName) {
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            Arguments = ArgumentsBuffer
                        }
                    };

                    DownloadProcess.OutputDataReceived += (s, e) => {
                        this.BeginInvoke(new MethodInvoker(() => {
                            if (e.Data != null && rtbConsoleOutput != null)
                                rtbConsoleOutput.AppendText(e.Data + "\n");
                        }));
                    };
                    DownloadProcess.ErrorDataReceived += (s, e) => {
                        this.BeginInvoke(new MethodInvoker(() => {
                            if (e.Data != null && rtbConsoleOutput != null) {
                                rtbConsoleOutput.AppendText("Error:\n");
                                rtbConsoleOutput.AppendText(e.Data + "\n");
                            }
                        }));
                    };

                    DownloadProcess.Start();

                    ArgumentsBuffer = null;
                    PreviewArguments = null;

                    DownloadProcess.BeginOutputReadLine();
                    DownloadProcess.BeginErrorReadLine();
                    DownloadProcess.WaitForExit();

                    if (DownloadProcess.ExitCode == 0) {
                        CurrentDownload.Status = DownloadStatus.Finished;
                    }
                    else {

                        CurrentDownload.Status = DownloadStatus.YtdlError;
                    }
                }
                catch (ThreadAbortException) {
                    this.BeginInvoke((MethodInvoker)delegate() {
                        CurrentDownload.Status = DownloadStatus.Aborted;
                        if (DownloadProcess != null && !DownloadProcess.HasExited) {
                            DownloadProcess.Kill();
                        }
                        System.Media.SystemSounds.Hand.Play();
                        rtbConsoleOutput.AppendText("Downloading was aborted by the user.");
                    });
                }
                catch (Exception ex) {
                    ErrorLog.ReportException(ex);
                    CurrentDownload.Status = DownloadStatus.ProgramError;
                }
                finally {
                    this.BeginInvoke((MethodInvoker)delegate() {
                        DownloadFinished();
                    });
                }
            });
            rtbConsoleOutput.AppendText("Created, starting download thread\n");
            DownloadThread.Name = "Downloading video";
            DownloadThread.Start();
            #endregion
        }
        private void DownloadFinished() {
            tmrTitleActivity.Stop();
            this.Text.Trim('.');
            btnDownloaderCancelExit.Text = lang.btnBatchDownloadExit;

            if (CurrentDownload.BatchDownload) {
                switch (CurrentDownload.Status) {
                    case DownloadStatus.Aborted:
                        if (AbortBatch) {
                            this.DialogResult = System.Windows.Forms.DialogResult.Abort;
                        }
                        else {
                            this.DialogResult = System.Windows.Forms.DialogResult.Ignore;
                        }
                        break;
                    case DownloadStatus.YtdlError:
                        this.Activate();
                        System.Media.SystemSounds.Hand.Play();
                        rtbConsoleOutput.AppendText("\nAn error occured\nTHIS IS A YOUTUBE-DL ERROR, NOT A ERROR WITH THIS PROGRAM!\nExit the form to resume batch download.");
                        this.Text = lang.frmDownloaderError;
                        break;
                    case DownloadStatus.ProgramError:
                        btnDownloaderCancelExit.Text = lang.btnDownloaderExit;
                        rtbConsoleOutput.AppendText("\nAn error occured\nAn error log was presented, if enabled.\nExit the form to resume batch download.");
                        this.Text = lang.frmDownloaderError;
                        this.Activate();
                        System.Media.SystemSounds.Hand.Play();
                        break;
                    case DownloadStatus.Finished:
                        this.DialogResult = System.Windows.Forms.DialogResult.Yes;
                        break;
                    default:
                        this.DialogResult = System.Windows.Forms.DialogResult.No;
                        break;
                }
            }
            else {
                switch (CurrentDownload.Status) {
                    case DownloadStatus.Aborted:
                        break;
                    case DownloadStatus.YtdlError:
                        btnDownloaderCancelExit.Text = lang.btnDownloaderExit;
                        rtbConsoleOutput.AppendText("\nAn error occured\nTHIS IS A YOUTUBE-DL ERROR, NOT A ERROR WITH THIS PROGRAM!");
                        btnDownloaderAbortBatchDownload.Visible = true;
                        btnDownloaderAbortBatchDownload.Enabled = true;
                        btnDownloaderAbortBatchDownload.Text = lang.GenericRetry;
                        this.Text = lang.frmDownloaderError;
                        this.Activate();
                        System.Media.SystemSounds.Hand.Play();
                        break;
                    case DownloadStatus.ProgramError:
                        btnDownloaderCancelExit.Text = lang.btnDownloaderExit;
                        rtbConsoleOutput.AppendText("\nAn error occured\nAn error log was presented, if enabled.");
                        this.Text = lang.frmDownloaderError;
                        this.Activate();
                        System.Media.SystemSounds.Hand.Play();
                        break;
                    case DownloadStatus.Finished:
                        btnDownloaderCancelExit.Text = lang.btnDownloaderExit;
                        this.Text = lang.frmDownloaderComplete;
                        rtbConsoleOutput.AppendText("Download has finished.");
                        if (chkDownloaderCloseAfterDownload.Checked) { this.Close(); }
                        break;
                    default:
                        btnDownloaderCancelExit.Text = lang.btnDownloaderExit;
                        this.Text = lang.frmDownloaderComplete;
                        rtbConsoleOutput.AppendText("CurrentDownload.Status not defined (Not a batch download)\nAssuming success.");
                        if (chkDownloaderCloseAfterDownload.Checked) { this.Close(); }
                        break;
                }
            }
        }
        private void tmrTitleActivity_Tick(object sender, EventArgs e) {
            if (this.Text.EndsWith("...."))
                this.Text = this.Text.TrimEnd('.');
            else
                this.Text += ".";
        }

        private void btnClearOutput_Click(object sender, EventArgs e) {
            rtbConsoleOutput.Clear();
        }

    }
}