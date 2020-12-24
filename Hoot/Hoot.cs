﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using System.IO;
using System.Xml.Serialization;
using System.Threading;
using System.Text.RegularExpressions;

namespace hOOt
{
    public class Hoot
    {
        public Hoot(string IndexPath, string FileName)
        {
            _Path = IndexPath;
            _FileName = FileName;
            if (_Path.EndsWith(Path.DirectorySeparatorChar.ToString()) == false) _Path += Path.DirectorySeparatorChar;
            Directory.CreateDirectory(IndexPath);
            LogManager.Configure(_Path + _FileName + ".txt", 200, false);
            _log.Debug("\r\n\r\n");
            _log.Debug("Starting hOOt....");
            _log.Debug("Storage Folder = " + _Path);

            _docs = new RaptorDBString(_Path + _FileName + ".docs", false);
            _bitmaps = new BitmapIndex(_Path, _FileName + ".mgbmp");
            _lastDocNum = (int)_docs.Count();
            // read words
            LoadWords();
            // read deleted
            _deleted = new BoolIndex(_Path, "_deleted.idx");
        }

        private SafeDictionary<string, int> _words = new SafeDictionary<string, int>();
        private BitmapIndex _bitmaps;
        private BoolIndex _deleted;
        private ILog _log = LogManager.GetLogger(typeof(Hoot));
        int _lastDocNum = 0;
        private string _FileName = "words";
        private string _Path = "";
        private RaptorDBString _docs;

        public int WordCount()
        {
            return _words.Count;
        }

        public int DocumentCount
        {
            get
            {
                return _lastDocNum;
            }
        }

        public void FreeMemory(bool freecache)
        {
            _log.Debug("freeing memory");
            // free deleted
            _deleted.FreeMemory();
        }

        public void Save()
        {
            InternalSave();
        }

        public void Index(int recordnumber, string text)
        {
            AddtoIndex(recordnumber, text);
        }

        public int Index(Document doc, bool deleteold)
        {
            _log.Debug("indexing doc : " + doc.FileName);
            DateTime dt = FastDateTime.Now;

            if (deleteold && doc.DocNumber > -1)
                _deleted.Set(true, doc.DocNumber);

            if (deleteold == true || doc.DocNumber == -1)
                doc.DocNumber = _lastDocNum++;

            // save doc to disk
            string dstr = fastJSON.JSON.Instance.ToJSON(doc, new fastJSON.JSONParameters { UseExtensions = false });
            _docs.Set(doc.FileName.ToLower(), Encoding.Unicode.GetBytes(dstr));

            _log.Debug("writing doc to disk (ms) = " + FastDateTime.Now.Subtract(dt).TotalMilliseconds);

            dt = FastDateTime.Now;
            // index doc
            AddtoIndex(doc.DocNumber, doc.Text);
            _log.Debug("indexing time (ms) = " + FastDateTime.Now.Subtract(dt).TotalMilliseconds);

            return _lastDocNum;
        }

        public IEnumerable<int> FindRows(string filter)
        {
            WAHBitArray bits = ExecutionPlan(filter);
            // enumerate records
            return bits.GetBitIndexes();
        }

        public IEnumerable<Document> FindDocuments(string filter)
        {
            WAHBitArray bits = ExecutionPlan(filter);
            // enumerate documents
            foreach (int i in bits.GetBitIndexes())
            {
                if (i > _lastDocNum - 1)
                    break;
                string b = _docs.ReadData(i);
                Document d = fastJSON.JSON.Instance.ToObject<Document>(b);

                yield return d;
            }
        }

        public IEnumerable<string> FindDocumentFileNames(string filter)
        {
            WAHBitArray bits = ExecutionPlan(filter);
            // enumerate documents
            foreach (int i in bits.GetBitIndexes())
            {
                if (i > _lastDocNum - 1)
                    break;
                string b = _docs.ReadData(i);
                var d = (Dictionary<string, object>)fastJSON.JSON.Instance.Parse(b);

                yield return d["FileName"].ToString();
            }
        }

        public void RemoveDocument(int number)
        {
            // add number to deleted bitmap
            _deleted.Set(true, number);
        }

        //public void OptimizeIndex()
        //{
        //    lock (_lock)
        //    {
        //        //_internalOP = true;
        //        InternalSave();
        //        _log.Debug("optimizing index..");
        //        DateTime dt = FastDateTime.Now;
        //        //_lastBitmapOffset = 0;
        //        //_bitmapFile.Flush();
        //        //_bitmapFile.Close();
        //        // compact bitmap index file to new file
        //        _bitmapFile = new FileStream(_Path + _FileName + ".bitmap$", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        //        MemoryStream ms = new MemoryStream();
        //        BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
        //        // save words and bitmaps
        //        using (FileStream words = new FileStream(_Path + _FileName + ".words", FileMode.Create))
        //        {
        //            foreach (KeyValuePair<string, int> kv in _wordindex)
        //            {
        //                bw.Write(kv.Key);
        //                uint[] ar = LoadBitmap(kv.Value.FileOffset);
        //                long offset = SaveBitmap(ar);
        //                kv.Value.FileOffset = offset;
        //                bw.Write(kv.Value.FileOffset);
        //            }
        //            // save words
        //            byte[] b = ms.ToArray();
        //            words.Write(b, 0, b.Length);
        //            words.Flush();
        //            words.Close();
        //        }
        //        // rename files
        //        _bitmapFile.Flush();
        //        _bitmapFile.Close();
        //        File.Delete(_Path + _FileName + ".bitmap");
        //        File.Move(_Path + _FileName + ".bitmap$", _Path + _FileName + ".bitmap");
        //        // reload everything
        //        _bitmapFile = new FileStream(_Path + _FileName + ".bitmap", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        //        _lastBitmapOffset = _bitmapFile.Seek(0L, SeekOrigin.End);
        //        _log.Debug("optimizing index done = " + DateTime.Now.Subtract(dt).TotalSeconds + " sec");
        //        _internalOP = false;
        //    }
        //}

        #region [  P R I V A T E   M E T H O D S  ]

        private WAHBitArray ExecutionPlan(string filter)
        {
            _log.Debug("query : " + filter);
            DateTime dt = FastDateTime.Now;
            // query indexes
            string[] words = filter.Split(' ');

            WAHBitArray bits = null;

            foreach (string s in words)
            {
                int c;
                string word = s;
                if (s == "") continue;

                OPERATION op = OPERATION.OR;

                if (s.StartsWith("+"))
                {
                    op = OPERATION.AND;
                    word = s.Replace("+", "");
                }

                if (s.StartsWith("-"))
                {
                    op = OPERATION.ANDNOT;
                    word = s.Replace("-", "");
                }

                if (s.Contains("*") || s.Contains("?"))
                {
                    WAHBitArray wildbits = null;
                    // do wildcard search
                    Regex reg = new Regex("^" + s.Replace("*", ".*").Replace("?", "."), RegexOptions.IgnoreCase);
                    foreach (string key in _words.Keys())
                    {
                        if (reg.IsMatch(key))
                        {
                            _words.TryGetValue(key, out c);
                            WAHBitArray ba = _bitmaps.GetBitmap(c);

                            wildbits = DoBitOperation(wildbits, ba, OPERATION.OR);
                        }
                    }
                    if (bits == null)
                        bits = wildbits;
                    else
                    {
                        if (op == OPERATION.AND)
                            bits = bits.And(wildbits);
                        else
                            bits = bits.Or(wildbits);
                    }
                }
                else if (_words.TryGetValue(word.ToLowerInvariant(), out c))
                {
                    // bits logic
                    WAHBitArray ba = _bitmaps.GetBitmap(c);
                    bits = DoBitOperation(bits, ba, op);
                }
            }
            if (bits == null)
                return new WAHBitArray();

            // remove deleted docs
            WAHBitArray ret = bits.AndNot(_deleted.GetBits());
            _log.Debug("query time (ms) = " + FastDateTime.Now.Subtract(dt).TotalMilliseconds);
            return ret;
        }

        private static WAHBitArray DoBitOperation(WAHBitArray bits, WAHBitArray c, OPERATION op)
        {
            if (bits != null)
            {
                switch (op)
                {
                    case OPERATION.AND:
                        bits = c.And(bits);
                        break;
                    case OPERATION.OR:
                        bits = c.Or(bits);
                        break;
                    case OPERATION.ANDNOT:
                        bits = c.AndNot(bits);
                        break;
                }
            }
            else
                bits = c;
            return bits;
        }

        private object _lock = new object();
        private void InternalSave()
        {
            lock (_lock)
            {
                _log.Debug("saving index...");
                DateTime dt = FastDateTime.Now;
                // save deleted
                _deleted.SaveIndex();

                // save docs 
                _docs.SaveIndex();
                _bitmaps.Commit(false);

                MemoryStream ms = new MemoryStream();
                BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);

                // save words and bitmaps
                using (FileStream words = new FileStream(_Path + _FileName + ".words", FileMode.Create))
                {
                    foreach (KeyValuePair<string, int> kv in _words)
                    {
                        bw.Write(kv.Key);
                        bw.Write(kv.Value);
                    }
                    byte[] b = ms.ToArray();
                    words.Write(b, 0, b.Length);
                    words.Flush();
                    words.Close();
                }
                _log.Debug("save time (ms) = " + FastDateTime.Now.Subtract(dt).TotalMilliseconds);
            }
        }

        private void LoadWords()
        {
            if (File.Exists(_Path + _FileName + ".words") == false)
                return;
            // load words
            byte[] b = File.ReadAllBytes(_Path + _FileName + ".words");
            MemoryStream ms = new MemoryStream(b);
            BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
            string s = br.ReadString();
            while (s != "")
            {
                int off = br.ReadInt32();
                _words.Add(s, off);
                try
                {
                    s = br.ReadString();
                }
                catch { s = ""; }
            }
            _log.Debug("Word Count = " + _words.Count);
        }

        private void AddtoIndex(int recnum, string text)
        {
            _log.Debug("text size = " + text.Length);
            Dictionary<string, int> wordfreq = GenerateWordFreq(text);
            _log.Debug("word count = " + wordfreq.Count);

            foreach (string key in wordfreq.Keys)
            {
                //Cache cache;
                int bmp;
                if (_words.TryGetValue(key, out bmp))
                {
                    _bitmaps.GetBitmap(bmp).Set(recnum, true);
                }
                else
                {
                    bmp = _bitmaps.GetFreeRecordNumber();
                    _bitmaps.SetDuplicate(bmp, recnum);
                    _words.Add(key, bmp);
                }
            }
        }

        private Dictionary<string, int> GenerateWordFreq(string text)
        {
            Dictionary<string, int> dic = new Dictionary<string, int>();//50000);

            char[] chars = text.ToCharArray();
            int index = 0;
            int run = -1;
            int count = chars.Length;
            while (index < count)
            {
                char c = chars[index++];
                if (!char.IsLetter(c))
                {
                    if (run != -1)
                    {
                        ParseString(dic, chars, index, run);
                        run = -1;
                    }
                }
                else
                    if (run == -1)
                        run = index - 1;
            }

            if (run != -1)
            {
                ParseString(dic, chars, index, run);
                run = -1;
            }

            return dic;
        }

        private void ParseString(Dictionary<string, int> dic, char[] chars, int end, int start)
        {
            // check if upper lower case mix -> extract words
            int uppers = 0;
            bool found = false;
            for (int i = start; i < end; i++)
            {
                if (char.IsUpper(chars[i]))
                    uppers++;
            }
            // not all uppercase
            if (uppers != end - start - 1)
            {
                int lastUpper = start;

                string word = "";
                for (int i = start + 1; i < end; i++)
                {
                    char c = chars[i];
                    if (char.IsUpper(c))
                    {
                        found = true;
                        word = new string(chars, lastUpper, i - lastUpper).ToLowerInvariant().Trim();
                        AddDictionary(dic, word);
                        lastUpper = i;
                    }
                }
                if (lastUpper > start)
                {
                    string last = new string(chars, lastUpper, end - lastUpper).ToLowerInvariant().Trim();
                    if (word != last)
                        AddDictionary(dic, last);
                }
            }
            if (found == false)
            {
                string s = new string(chars, start, end - start - 1).ToLowerInvariant().Trim();
                AddDictionary(dic, s);
            }
        }

        private void AddDictionary(Dictionary<string, int> dic, string word)
        {
            int l = word.Length;
            if (l > Global.DefaultStringKeySize)// MAX_STRING_LENGTH_IGNORE)
                return;
            if (l < 2)
                return;
            if (char.IsLetter(word[l - 1]) == false)
                word = new string(word.ToCharArray(), 0, l - 2);
            if (word.Length < 2)
                return;
            int cc = 0;
            if (dic.TryGetValue(word, out cc))
                dic[word] = ++cc;
            else
                dic.Add(word, 1);
        }
        #endregion
    }
}
