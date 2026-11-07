// Портировано из shazam-for-real (https://github.com/AlekseyMartynov/shazam-for-real)
// ISC License, Copyright (c) Aleksey Martynov
using System;

namespace Beg1nnnerOS.Shazam;

class ShazamResult {
    public bool Success;
    public string ID;
    public string AppleSongID;
    public string Artist;
    public string Title;
    public string Url;
    public int RetryMs;
}
