﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace LibXboxOne
{
    public class XvdFile : IDisposable
    {
        public static bool CikFileLoaded = false;
        public static Dictionary<Guid, byte[]> CikKeys = new Dictionary<Guid, byte[]>();
        public static Guid NullGuid = new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public static byte[] TestCikHash = new byte[] { 0xb7, 0x18, 0x3b, 0x86, 0x83, 0x56, 0x43, 0x02, 0x99, 0xe5, 0xef, 0xcb, 0xa9, 0xe8, 0x34, 0x97, 0x5d, 0x78, 0xcf, 0x6f };

        public static Guid GetTestCikKey()
        {
            foreach (var kvp in CikKeys)
            {
                byte[] guidBytes = kvp.Key.ToByteArray();
                byte[] guidHash = SHA1.Create().ComputeHash(guidBytes);
                if (guidHash.IsEqualTo(TestCikHash))
                {
                    return kvp.Key;
                }
            }

            return NullGuid;
        }

        public static bool OdkKeyLoaded = false;
        public static byte[] OdkKey;

        public static bool SignKeyLoaded = false;
        public static byte[] SignKey;

        public static bool DisableNativeFunctions = false;
        public static bool DisableDataHashChecking = false;

        public XvdHeader Header;
        public XvcInfo XvcInfo;

        public List<XvcRegionHeader> RegionHeaders;
        public List<XvcUpdateSegmentInfo> UpdateSegments;

        public ulong HashTreeOffset;
        public ulong HashTreeBlockCount;
        public ulong HashTreeLevels;
        public ulong UserDataOffset;
        public ulong DataOffset;
        public ulong XvdDataBlockCount;

        public bool HashTreeValid = false;
        public bool DataHashTreeValid = false;
        public bool XvcDataHashValid = false;

        public bool CikIsDecrypted = false;

        public static uint[] XvcContentTypes = 
        { // taken from bit test 0x07018042 (00000111000000011000000001000010)
          // idx of each set bit (from right to left) is an XVC-enabled content type
            0x1,
            0x6,
            0xF,
            0x10,
            0x18,
            0x19,
            0x1A
        };

        private readonly IO _io;
        private readonly string _filePath;

        public string FilePath
        {
            get { return _filePath; }
        }

        public DateTime TimeCreated
        {
            get { return DateTime.FromFileTime(Header.FileTimeCreated); }
        }

        public bool IsXvcFile
        {
            get { return Header.ContentType < 0x1A && XvcContentTypes.Contains(Header.ContentType); }
        }

        public bool IsEncrypted
        {
            get { return !Header.VolumeFlags.IsFlagSet((uint) XvdVolumeFlags.EncryptionDisabled); }
        }

        public bool IsDataIntegrityEnabled
        {
            get { return !Header.VolumeFlags.IsFlagSet((uint)XvdVolumeFlags.DataIntegrityDisabled); }
        }

        private bool CryptHeaderCik(bool encrypt)
        {
            if ((!encrypt && CikIsDecrypted) || IsXvcFile)
            {
                CikIsDecrypted = true;
                return true;
            }

            if (!OdkKeyLoaded)
                return false;

            var cipher = new AesCipher(OdkKey);
            if (encrypt)
            {
                cipher.EncryptBlock(Header.EncryptedCIK, 0, 0x10, Header.EncryptedCIK, 0);
                cipher.EncryptBlock(Header.EncryptedCIK, 0x10, 0x10, Header.EncryptedCIK, 0x10);
            }
            else
            {
                cipher.DecryptBlock(Header.EncryptedCIK, 0, 0x10, Header.EncryptedCIK, 0);
                cipher.DecryptBlock(Header.EncryptedCIK, 0x10, 0x10, Header.EncryptedCIK, 0x10);
            }

            CikIsDecrypted = !encrypt;

            return true;
        }

        private bool CryptXvcRegion(int regionIdx, bool encrypt)
        {
            if (XvcInfo.ContentID == null || !XvcInfo.IsAnyKeySet || regionIdx > XvcInfo.RegionCount || RegionHeaders == null || regionIdx >= RegionHeaders.Count)
                return false;

            XvcRegionHeader header = RegionHeaders[regionIdx];
            if (encrypt && header.KeyId == 0xFFFF)
                header.KeyId = 0;

            if (header.Length <= 0 || header.Offset <= 0 || header.KeyId == 0xFFFF || (header.KeyId+1) > XvcInfo.KeyCount)
                return false;

            byte[] key;
            GetXvcKey(header.KeyId, out key);

            if (key == null)
                return false;

            return CryptSection(encrypt, key, header.Id, header.Offset, header.Length);
        }

        private bool CryptSection(bool encrypt, byte[] key, uint headerId, ulong offset, ulong length)
        {
            var ivKey = new byte[0x10];
            var dataKey = new byte[0x10];
            Array.Copy(key, ivKey, 0x10);
            Array.Copy(key, 0x10, dataKey, 0, 0x10);

            var counterToEncrypt = new byte[0x10];
            Array.Copy(Header.VDUID, 0, counterToEncrypt, 0x8, 0x8);
            byte[] test = BitConverter.GetBytes(headerId);
            Array.Copy(test, 0, counterToEncrypt, 0x4, 0x4);

            int numBlocks = (int)(length + 0xFFF) / 0x1000;

            var ivCipher = new AesCipher(ivKey);

            _io.Stream.Position = (long)offset;
            for (int i = 0; i < numBlocks; i++)
            {
                var counter = new byte[0x10];
                Array.Copy(counterToEncrypt, counter, 0x10);

                var blockIdBytes = BitConverter.GetBytes(i);
                Array.Copy(blockIdBytes, counter, 4);

                var counterEnc = new byte[0x10];
                ivCipher.EncryptBlock(counter, 0, 0x10, counterEnc, 0);

                byte[] origData = _io.Reader.ReadBytes(0x1000);
                byte[] newData = Shared.CryptData(encrypt, origData, dataKey, counterEnc);

                _io.Stream.Position = _io.Stream.Position - 0x1000;
                _io.Writer.Write(newData);
            }
            return true;
        }

        private ulong CalculateHashBlockNumForBlockNum(uint unk1, ulong blockNum, uint idx, out ulong entryNumInBlock)
        {
            var tempHashTreeLevels = HashTreeLevels;

            entryNumInBlock = 0;

            if (unk1 > 1)
                return 0;

            uint edx;

            ulong returnVal = 0;
            if (idx == 0)
            {
                ulong newBlock = blockNum*0xc0c0c0c1;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                returnVal = edx >> 7;

                var eax = returnVal * 0xAA;
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                edx = (uint)(eax >> 32);
                eax = (uint)(eax & uint.MaxValue);

                blockNum -= eax;
                entryNumInBlock = blockNum;
                tempHashTreeLevels--;

                if (tempHashTreeLevels == 0)
                    return returnVal;

                var addr = XvdDataBlockCount + 0x70E3;

                addr = addr * 0x9121b243;
                edx = (uint)(addr >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                addr = (uint)(addr & uint.MaxValue);

                edx = edx >> 0xE;
                returnVal += edx;
                tempHashTreeLevels--;
            }
            if (idx == 1)
            {
                var newBlock = blockNum * 0xc0c0c0c1;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                ulong ecx = edx;
                ecx = ecx >> 7;

                var newEcx = ecx * 0xc0c0c0c1;
                edx = (uint)(newEcx >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newEcx = (uint)(newEcx & uint.MaxValue);

                edx = edx >> 7;

                var eax = (ulong)edx * 0xAA;
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                edx = (uint)(eax >> 32);
                eax = (uint)(eax & uint.MaxValue);

                ecx -= eax;

                entryNumInBlock = ecx;

                newBlock = blockNum * 0x9121b243;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                returnVal = edx;
                returnVal = returnVal >> 0xE;
                tempHashTreeLevels -= 2;
            }
            if (idx == 0 || idx == 1)
            {
                if (tempHashTreeLevels == 0)
                    return returnVal;

                var addr = XvdDataBlockCount + 0x4AF767;

                addr = addr*0xDA8D187D;
                edx = (uint)(addr >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                addr = (uint)(addr & uint.MaxValue);

                tempHashTreeLevels--;
                edx = edx >> 0x16;
                returnVal += edx;
            }
            if (idx == 2)
            {
                var newBlock = blockNum * 0x9121B243;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                ulong ecx = edx;
                ecx = ecx >> 0xE;

                var newEcx = ecx * 0xc0c0c0c1;
                edx = (uint)(newEcx >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newEcx = (uint)(newEcx & uint.MaxValue);

                edx = edx >> 7;

                var eax = (ulong)edx * 0xAA;
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                edx = (uint)(eax >> 32);
                eax = (uint)(eax & uint.MaxValue);

                ecx -= eax;

                entryNumInBlock = ecx;

                newBlock = blockNum * 0xda8d187d;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                returnVal = edx;
                tempHashTreeLevels -= 3;
                returnVal = returnVal >> 0x16;
            }
            if (idx == 0 || idx == 1 || idx == 2)
            {
                if (tempHashTreeLevels == 0)
                    return returnVal;

                var addr = XvdDataBlockCount + 0x31C84B0F;

                var newAddr = addr * 0x491CC17D;
                edx = (uint)(newAddr >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newAddr = (uint)(newAddr & uint.MaxValue);

                addr -= edx;
                addr = addr >> 1;
                addr += edx;
                addr = addr >> 0x1D;
                returnVal += addr;
                return returnVal;
            }
            if (idx == 3)
            {
                var newBlock = blockNum * 0xDA8D187D;
                edx = (uint)(newBlock >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newBlock = (uint)(newBlock & uint.MaxValue);

                ulong ecx = edx;
                ecx = ecx >> 0x16;

                var newEcx = ecx * 0xc0c0c0c1;
                edx = (uint)(newEcx >> 32);
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                newEcx = (uint)(newEcx & uint.MaxValue);

                edx = edx >> 7;

                ulong eax = (ulong)edx * 0xAA;
                // ReSharper disable once UnusedVariable
                // ReSharper disable once RedundantAssignment
                edx = (uint)(eax >> 32);
                eax = (uint)(eax & uint.MaxValue);

                ecx -= eax;
                entryNumInBlock = ecx;
                returnVal = 0;
            }
            return returnVal;
        }

        private ulong CalculateNumHashBlocksInLevel(ulong size, ulong idx)
        {
            var tempSize = size;
            if (idx == 0)
            {
                tempSize += 0xA9;
                tempSize = tempSize * 0xc0c0c0c1;
                return (tempSize >> 32) / 0x80;
            }
            if (idx == 1)
            {
                tempSize += 0x70e3;
                tempSize = tempSize * 0x9121b243;
                return (tempSize >> 32) / 0x4000;
            }
            if (idx == 2)
            {
                tempSize += 0x4af767;
                tempSize = tempSize * 0xDA8D187D;
                return (tempSize >> 32) / 0x400000;
            }
            if (idx == 3)
            {
                tempSize += 0x31C84B0F;
                tempSize = tempSize * 0x491CC17D;
                var high = (tempSize >> 32);
                var low = (tempSize & uint.MaxValue);
                low -= high;
                low = low / 2;
                low += high;
                return low / 0x20000000;
            }
            return 0;
        }

        public byte[] ExtractEmbeddedXvd()
        {
            if (Header.EmbeddedXVDLength == 0)
                return null;
            _io.Stream.Position = 3 * 0x1000; // end of XVD header
            return _io.Reader.ReadBytes((int)Header.EmbeddedXVDLength);
        }

        public byte[] ExtractUserData()
        {
            if (Header.UserDataLength == 0)
                return null;
            _io.Stream.Position = (long)UserDataOffset;
            return _io.Reader.ReadBytes((int) Header.UserDataLength);
        }

        private void CalculateDataOffsets()
        {
            ulong[] longs = { Header.UserDataLength, Header.XvcDataLength, Header.DynamicHeaderLength, Header.DriveSize };
            // count up how many blocks each of the sections lengths in the header take up
            XvdDataBlockCount = longs.Select(addLong => (addLong + 0xFFF) / 0x1000).Aggregate<ulong, ulong>(0, (current, newLong) => current + newLong);

            ulong total2 = XvdDataBlockCount + 0xa9;
            total2 = total2 * 0xC0C0C0C1;

            var high = (uint)(total2 >> 32);
            // ReSharper disable once UnusedVariable
            var low = (uint)(total2 & uint.MaxValue);

            HashTreeBlockCount = high / 0x80;
            if (HashTreeBlockCount > 1)
            {
                HashTreeLevels = 1;
                ulong result = 2;
                while (result > 1)
                {
                    result = CalculateNumHashBlocksInLevel(XvdDataBlockCount, HashTreeLevels);
                    HashTreeLevels += 1;
                    HashTreeBlockCount += result;
                }
            }

            var userDataBlocks = (Header.UserDataLength + 0xFFF) / 0x1000;
            var exvdBlocks = (Header.EmbeddedXVDLength + 0xFFF) / 0x1000;
            var totalBlocks = 3 + exvdBlocks;
            HashTreeOffset = totalBlocks * 0x1000;
            UserDataOffset = (totalBlocks + HashTreeBlockCount) * 0x1000;

            if (!IsDataIntegrityEnabled)
                UserDataOffset = HashTreeOffset;

            DataOffset = UserDataOffset + (userDataBlocks * 0x1000);
        }

        public XvdFile(string path)
        {
            _filePath = path;
            _io = new IO(path);

            LoadKeysFromDisk();
        }

        public static void LoadKeysFromDisk()
        {
            if (!CikFileLoaded)
            {
                string cikFile = Shared.FindFile("cik_keys.bin");
                if (!String.IsNullOrEmpty(cikFile))
                {
                    using(var cikIo = new IO(cikFile))
                    {
                        if (cikIo.Stream.Length >= 0x30)
                        {
                            var numKeys = (int)(cikIo.Stream.Length/0x30);
                            for (int i = 0; i < numKeys; i++)
                            {
                                var keyGuid = new Guid(cikIo.Reader.ReadBytes(0x10));
                                byte[] key = cikIo.Reader.ReadBytes(0x20);
                                CikKeys.Add(keyGuid, key);
                            }
                            CikFileLoaded = true;
                        }
                    }
                    GetTestCikKey();
                }
            }
            if (!OdkKeyLoaded)
            {
                string odkFile = Shared.FindFile("odk_key.bin");
                if (!String.IsNullOrEmpty(odkFile))
                {
                    byte[] testKey = File.ReadAllBytes(odkFile);
                    if (testKey.Length >= 0x20)
                    {
                        OdkKey = testKey;
                        OdkKeyLoaded = true;
                    }
                }
            }
            if (!SignKeyLoaded)
            {
                string keyFile = Shared.FindFile("rsa3_key.bin");
                if (!String.IsNullOrEmpty(keyFile))
                {
                    byte[] testKey = File.ReadAllBytes(keyFile);
                    if (testKey.Length >= 0x91B)
                    {
                        SignKey = testKey;
                        SignKeyLoaded = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            _io.Dispose();
        }

        public bool Decrypt()
        {
            if (!IsEncrypted)
                return true;

            CryptHeaderCik(false);
            if (!CikIsDecrypted)
                return false;

            bool success;

            if (IsXvcFile)
            {
                for (int i = 0; i < RegionHeaders.Count; i++)
                {
                    XvcRegionHeader header = RegionHeaders[i];
                    if (header.Length <= 0 || header.Offset <= 0 || header.KeyId == 0xFFFF || (header.KeyId + 1) > XvcInfo.KeyCount)
                        continue;
                    if (!CryptXvcRegion(i, false))
                        return false;
                }
                success = true;
            }
            else
            {
                // todo: check with more non-xvc xvds and see if they use any other headerId besides 0x1
                success = CryptSection(false, Header.EncryptedCIK, 0x1, UserDataOffset,
                    (ulong)_io.Stream.Length - UserDataOffset);
            }

            if (!success)
                return false;

            Header.VolumeFlags = Header.VolumeFlags.ToggleFlag((uint) XvdVolumeFlags.EncryptionDisabled);
            Save();

            return true;
        }

        public bool Encrypt(int cikKeyId = 0)
        {
            if (IsEncrypted)
                return true;

            bool success;

            if (!IsXvcFile)
            {
                if (Header.EncryptedCIK.IsArrayEmpty())
                {
                    // generate a new CIK if there's none specified
                    var rng = new Random();
                    Header.EncryptedCIK = new byte[0x20];
                    rng.NextBytes(Header.EncryptedCIK);
                }

                // todo: check with more non-xvc xvds and see if they use any other headerId besides 0x1
                success = CryptSection(true, Header.EncryptedCIK, 0x1, UserDataOffset,
                    (ulong)_io.Stream.Length - UserDataOffset);
            }
            else
            {
                if (cikKeyId >= 0) // if cikKeyId isn't -1 set the XvcInfo key GUID to one we know
                {
                    var keyGuids = CikKeys.Keys.ToList();
                    if (cikKeyId < 0 || cikKeyId >= keyGuids.Count)
                        return false;

                    XvcInfo.EncryptionKeyIds[0].KeyId = keyGuids[cikKeyId].ToByteArray();
                }

                for (int i = 0; i < RegionHeaders.Count; i++)
                {
                    XvcRegionHeader header = RegionHeaders[i];
                    if (header.Length <= 0 || header.Offset <= 0 || ((header.KeyId + 1) > XvcInfo.KeyCount && header.KeyId != 0xFFFF))
                        continue;

                    if (header.Id == 0x40000005 || header.Id == 0x40000004 || header.Id == 0x40000001)
                        continue; // skip XVD header / EXVD / XVC info

                    if (!CryptXvcRegion(i, true))
                        return false;
                }
                success = true;
            }

            if (!success)
                return false;

            CryptHeaderCik(true);

            Header.VolumeFlags = Header.VolumeFlags.ToggleFlag((uint)XvdVolumeFlags.EncryptionDisabled);

            // seems the readonly flag gets set when encrypting
            if (!Header.VolumeFlags.IsFlagSet((uint) XvdVolumeFlags.ReadOnly))
            {
                Header.VolumeFlags = Header.VolumeFlags.ToggleFlag((uint)XvdVolumeFlags.ReadOnly);
            }

            Save();

            return true;
        }

        public bool Save()
        {
            CalculateDataOffsets();

            if (Header.XvcDataLength > 0 && IsXvcFile)
            {
                _io.Stream.Position = (long)DataOffset;

                XvcInfo.RegionCount = (uint)RegionHeaders.Count;
                XvcInfo.UpdateSegmentCount = (uint)UpdateSegments.Count;

                _io.Writer.WriteStruct(XvcInfo);

                for (int i = 0; i < XvcInfo.RegionCount; i++)
                    _io.Writer.WriteStruct(RegionHeaders[i]);

                for (int i = 0; i < XvcInfo.UpdateSegmentCount; i++)
                    _io.Writer.WriteStruct(UpdateSegments[i]);
            }

            if (IsDataIntegrityEnabled)
            {
// ReSharper disable once UnusedVariable
                int[] invalidBlocks = VerifyDataHashTree(true);
// ReSharper disable once UnusedVariable
                bool hashTreeValid = CalculateHashTree();
            }

            _io.Stream.Position = 0;
            _io.Writer.WriteStruct(Header);

            return true;
        }

        public bool Load()
        {
            _io.Stream.Position = 0;
            Header = _io.Reader.ReadStruct<XvdHeader>();

            CikIsDecrypted = !IsEncrypted;

            CalculateDataOffsets();

            if (DataOffset >= (ulong)_io.Stream.Length)
                return false;

            if (Header.XvcDataLength > 0 && IsXvcFile)
            {
                _io.Stream.Position = (long)DataOffset;

                XvcInfo = _io.Reader.ReadStruct<XvcInfo>();

                if (XvcInfo.Version == 1)
                {
                    RegionHeaders = new List<XvcRegionHeader>();
                    for (int i = 0; i < XvcInfo.RegionCount; i++)
                        RegionHeaders.Add(_io.Reader.ReadStruct<XvcRegionHeader>());

                    UpdateSegments = new List<XvcUpdateSegmentInfo>();
                    for (int i = 0; i < XvcInfo.UpdateSegmentCount; i++)
                        UpdateSegments.Add(_io.Reader.ReadStruct<XvcUpdateSegmentInfo>());
                }
            }

            DataHashTreeValid = true;
            HashTreeValid = true;

            if (IsDataIntegrityEnabled)
            {
                if (!DisableDataHashChecking)
                {
                    int[] invalidBlocks = VerifyDataHashTree();
                    DataHashTreeValid = invalidBlocks.Length <= 0;
                }
                HashTreeValid = VerifyHashTree();
            }

            XvcDataHashValid = VerifyXvcHash();
            return true;
        }

        public bool VerifyXvcHash(bool rehash = false)
        {
            if (!IsXvcFile)
                return true;

            ulong hashTreeSize = HashTreeBlockCount * 0x1000;

            var ms = new MemoryStream();
            var msIo = new IO(ms);
            msIo.Writer.WriteStruct(XvcInfo);
            for (int i = 0; i < XvcInfo.RegionCount; i++)
                msIo.Writer.WriteStruct(RegionHeaders[i]);

            for (int i = 0; i < XvcInfo.UpdateSegmentCount; i++)
                msIo.Writer.WriteStruct(UpdateSegments[i]);

            msIo.Stream.SetLength(Header.XvcDataLength);

            // fix region headers to match pre-hashtable
            msIo.Stream.Position = 0xDA8 + 0x50;
            for (int i = 0; i < XvcInfo.RegionCount; i++)
            {
                ulong length = RegionHeaders[i].Length;
                ulong offset = RegionHeaders[i].Offset;

                if (IsDataIntegrityEnabled)
                {
                    if ((offset == HashTreeOffset || offset < HashTreeOffset) && HashTreeOffset < (offset + length))
                        length -= hashTreeSize;
                    else if (offset > HashTreeOffset)
                        offset -= hashTreeSize;
                }

                msIo.Writer.Write(offset); // write fixed offset
                msIo.Writer.Write(length); // write fixed length
                msIo.Writer.Write((ulong)0); // null out PDUID

                msIo.Stream.Position += (0x80 - 24);
            }


            if (IsDataIntegrityEnabled)
            {
                // remove hash table offset from the special regions
                if (XvcInfo.InitialPlayOffset > HashTreeOffset)
                {
                    msIo.Stream.Position = 0xD28;
                    msIo.Writer.Write(XvcInfo.InitialPlayOffset - hashTreeSize);
                }

                if (XvcInfo.PreviewOffset > HashTreeOffset)
                {
                    msIo.Stream.Position = 0xD40;
                    msIo.Writer.Write(XvcInfo.PreviewOffset - hashTreeSize);
                }
            }

            byte[] xvcData = ms.ToArray();
            msIo.Dispose();
            byte[] hash = SHA256.Create().ComputeHash(xvcData);
            bool isValid = Header.OriginalXvcDataHash.IsEqualTo(hash);

            if (rehash)
                Header.OriginalXvcDataHash = hash;

            return isValid; //todo: investigate why this gets the correct hash for dev XVCs but fails for retail ones, might be to do with retail XVC data having a content ID that doesn't match with VDUID/UDUID
        }

        public bool AddHashTree()
        {
            if (IsDataIntegrityEnabled)
                return true;

            var hashTreeSize = (long) (HashTreeBlockCount * 0x1000);

            _io.Stream.Position = (long)HashTreeOffset;
            if (!_io.AddBytes(hashTreeSize))
                return false;

            Header.VolumeFlags = Header.VolumeFlags.ToggleFlag((uint)XvdVolumeFlags.DataIntegrityDisabled);

            if (IsXvcFile)
            {
                if (XvcInfo.InitialPlayOffset > 0)
                    XvcInfo.InitialPlayOffset += (ulong) hashTreeSize;

                if (XvcInfo.PreviewOffset > 0)
                    XvcInfo.PreviewOffset += (ulong) hashTreeSize;

                foreach (XvcRegionHeader t in RegionHeaders)
                {
                    XvcRegionHeader header = t; // have to make a new object pointer otherwise c# complains

                    ulong regionEnd = header.Offset + header.Length;

                    if ((header.Offset == HashTreeOffset || header.Offset < HashTreeOffset) &&
                        HashTreeOffset < regionEnd)
                        header.Length += (ulong) hashTreeSize;
                    else if (header.Offset > HashTreeOffset)
                        header.Offset += (ulong) hashTreeSize;

                    header.RegionPDUID = 0;
                }
            }

            CalculateDataOffsets();
            return Save();

            // todo: figure out update segments and fix them

            //VerifyDataHashTree(true);
            //return CalculateHashTree();
        }

        public bool RemoveHashTree()
        {
            if (!IsDataIntegrityEnabled)
                return true;

            var hashTreeSize = (long)(HashTreeBlockCount * 0x1000);

            _io.Stream.Position = (long)HashTreeOffset;
            if (!_io.DeleteBytes(hashTreeSize))
                return false;

            Header.VolumeFlags = Header.VolumeFlags.ToggleFlag((uint)XvdVolumeFlags.DataIntegrityDisabled);
            
            for (int i = 0; i < Header.TopHashBlockHash.Length; i++)
                Header.TopHashBlockHash[i] = 0;

            if (IsXvcFile)
            {
                if (XvcInfo.InitialPlayOffset > HashTreeOffset)
                    XvcInfo.InitialPlayOffset -= (ulong) hashTreeSize;

                if (XvcInfo.PreviewOffset > HashTreeOffset)
                    XvcInfo.PreviewOffset -= (ulong) hashTreeSize;

                for(int i = 0; i < RegionHeaders.Count; i++)
                {
                    var newHdr = new XvcRegionHeader(RegionHeaders[i]);

                    ulong regionEnd = newHdr.Offset + newHdr.Length;

                    if ((newHdr.Offset == HashTreeOffset || newHdr.Offset < HashTreeOffset) && HashTreeOffset < regionEnd)
                        newHdr.Length -= (ulong)hashTreeSize;
                    else if (newHdr.Offset > HashTreeOffset)
                        newHdr.Offset -= (ulong)hashTreeSize;

                    newHdr.RegionPDUID = 0;

                    RegionHeaders[i] = newHdr;
                }
            }

            // todo: figure out update segments and fix them

            return true;
        }

        public int[] VerifyDataHashTree(bool rehash = false)
        {
            int dataBlockCount = (int)((ulong)_io.Stream.Length - UserDataOffset)/0x1000;
            var invalidBlocks = new List<int>();

            for (int i = 0; i < dataBlockCount; i++)
            {
                ulong stackNum;
                var blockNum = CalculateHashBlockNumForBlockNum(Header.Unknown1_HashTableRelated, (ulong)i, 0, out stackNum);

                var hashEntryOffset = (blockNum*0x1000) + HashTreeOffset;
                hashEntryOffset += stackNum*0x18;

                _io.Stream.Position = (long)hashEntryOffset;
                byte[] oldhash = _io.Reader.ReadBytes(0x18);

                var dataToHashOffset = (((uint)i*0x1000) + UserDataOffset);

                _io.Stream.Position = (long)dataToHashOffset;
                byte[] data = _io.Reader.ReadBytes(0x1000);
                byte[] hash = SHA256.Create().ComputeHash(data);
                Array.Resize(ref hash, 0x18);

                bool writeIdx = false; // encrypted data uses 0x14 hashes with a block IDX added to the end to make the 0x18 hash
                var idxToWrite = (uint)i;
                if (IsEncrypted)
                {
                    if (IsXvcFile)
                    {
                        var hdr = new XvcRegionHeader();
                        foreach (var region in RegionHeaders)
                        {
                            if (region.KeyId == 0xFFFF)
                                continue; // skip unencrypted regions

                            if (dataToHashOffset >= region.Offset && dataToHashOffset < (region.Offset + region.Length))
                            {
                                writeIdx = true;
                                hdr = region;
                                break;
                            }
                        }
                        if (hdr.Id != 0)
                        {
                            var regionOffset = dataToHashOffset - hdr.Offset;
                            var regionBlockNo = (regionOffset + 0xFFF)/0x1000;
                            idxToWrite = (uint) regionBlockNo;
                        }
                    }
                    else
                    {
                        writeIdx = true;
                        idxToWrite = (uint) i;
                    }
                }

                if (writeIdx)
                {
                    byte[] idxBytes = BitConverter.GetBytes(idxToWrite);
                    Array.Copy(idxBytes, 0, hash, 0x14, 4);
                }

                if (hash.IsEqualTo(oldhash))
                    continue;

                invalidBlocks.Add(i);
                if (!rehash)
                    continue;
                _io.Stream.Position = (long)hashEntryOffset;
                _io.Writer.Write(hash);
            }

            return invalidBlocks.ToArray();
        }

        public bool CalculateHashTree()
        {
            uint blocksPerLevel = 0xAA;
            uint hashTreeLevel = 1;
            while (hashTreeLevel < HashTreeLevels)
            {
                uint dataBlockNum = 0;
                if (XvdDataBlockCount != 0)
                {
                    while (dataBlockNum < XvdDataBlockCount)
                    {
                        ulong entryNum;
                        var blockNum = CalculateHashBlockNumForBlockNum(Header.Unknown1_HashTableRelated, dataBlockNum, hashTreeLevel - 1, out entryNum);
                        _io.Stream.Position = (long)(HashTreeOffset + (blockNum * 0x1000));
                        byte[] blockHash = SHA256.Create().ComputeHash(_io.Reader.ReadBytes(0x1000));
                        Array.Resize(ref blockHash, 0x18);

                        ulong entryNum2;
                        var secondBlockNum = CalculateHashBlockNumForBlockNum(Header.Unknown1_HashTableRelated, dataBlockNum, hashTreeLevel, out entryNum2);
                        
                        var hashEntryOffset = HashTreeOffset + (secondBlockNum*0x1000);
                        hashEntryOffset += (entryNum2 + (entryNum2 * 2)) << 3;
                        _io.Stream.Position = (long)hashEntryOffset;

                        byte[] oldHash = _io.Reader.ReadBytes(0x18);
                        if (!blockHash.IsEqualTo(oldHash))
                        {
                            _io.Stream.Position = (long)hashEntryOffset; // todo: maybe return a list of blocks that needed rehashing
                            _io.Writer.Write(blockHash);
                        }

                        dataBlockNum += blocksPerLevel;
                    }
                }
                hashTreeLevel++;
                blocksPerLevel = blocksPerLevel * 0xAA;
            }
            _io.Stream.Position = (long)HashTreeOffset;
            byte[] hash = SHA256.Create().ComputeHash(_io.Reader.ReadBytes(0x1000));
            Header.TopHashBlockHash = hash;

            return true;
        }

        public bool VerifyHashTree()
        {
            if (!IsDataIntegrityEnabled)
                return true;

            _io.Stream.Position = (long)HashTreeOffset;
            byte[] hash = SHA256.Create().ComputeHash(_io.Reader.ReadBytes(0x1000));
            if (!Header.TopHashBlockHash.IsEqualTo(hash))
                return false;

            if (HashTreeLevels == 1)
                return true;

            var blocksPerLevel = 0xAA;
            ulong topHashTreeBlock = 0;
            uint hashTreeLevel = 1;
            while (hashTreeLevel < HashTreeLevels)
            {
                uint dataBlockNum = 0;
                if (XvdDataBlockCount != 0)
                {
                    while (dataBlockNum < XvdDataBlockCount)
                    {
                        ulong entryNum;
                        var blockNum = CalculateHashBlockNumForBlockNum(Header.Unknown1_HashTableRelated, dataBlockNum, hashTreeLevel - 1, out entryNum);

                        _io.Stream.Position = (long) (HashTreeOffset + (blockNum*0x1000));
                        byte[] blockHash = SHA256.Create().ComputeHash(_io.Reader.ReadBytes(0x1000));
                        Array.Resize(ref blockHash, 0x18);

                        ulong entryNum2;
                        var secondBlockNum = CalculateHashBlockNumForBlockNum(Header.Unknown1_HashTableRelated, dataBlockNum, hashTreeLevel, out entryNum2);
                        topHashTreeBlock = secondBlockNum;

                        var hashEntryOffset = HashTreeOffset + (secondBlockNum * 0x1000);
                        hashEntryOffset += (entryNum2 + (entryNum2 * 2)) << 3;
                        _io.Stream.Position = (long)hashEntryOffset;

                        byte[] expectedHash = _io.Reader.ReadBytes(0x18);
                        if (!expectedHash.IsEqualTo(blockHash))
                        {
                            // wrong hash
                            return false;
                        }
                        dataBlockNum += (uint)blocksPerLevel;
                    }
                }
                hashTreeLevel++;
                blocksPerLevel = blocksPerLevel * 0xAA;
            }
            if (topHashTreeBlock != 0)
            {
                Console.WriteLine(@"Top level hash page calculated to be at {0}, should be 0!", topHashTreeBlock);
            }
            return true;
        }


        public bool ConvertToVhd(string destFile)
        {
            if (IsEncrypted)
                return false;

            // xvd -> vhd conversion just needs the FS data extracted with a vhd footer put at the end
            // seems to work for older OS XVDs, newer XVCs use a 4k sector size though and windows VHD driver doesn't like it, not sure what new OS XVDs use
            // todo: look into converting 4k sectors to 512

            using(var vhdFile = new IO(destFile, FileMode.Create))
            {
                _io.Stream.Position = (long)DataOffset;

                if (IsXvcFile) // if it's an XVC file then find the correct XVC data offset
                {
                    bool foundFs = false;
                    foreach (var hdr in RegionHeaders)
                    {
                        if (hdr.Description == "FS-MD" || hdr.Id == 0x40000002)
                        {
                            foundFs = true;
                            _io.Stream.Position = (long)hdr.Offset;
                            break;
                        }
                    }
                    if (!foundFs)
                    { // no FS-MD in the file, older XVCs seem to use the last region for it
                        _io.Stream.Position = (long)RegionHeaders[RegionHeaders.Count - 1].Offset;
                    }
                }

                vhdFile.Stream.Position = 0;
                var driveSize = (long)Header.DriveSize;

                while (_io.Stream.Length > _io.Stream.Position)
                {
                    long toRead = 0x4000;
                    if (_io.Stream.Position + toRead > _io.Stream.Length)
                        toRead = _io.Stream.Length - _io.Stream.Position;

                    byte[] data = _io.Reader.ReadBytes((int) toRead);
                    vhdFile.Writer.Write(data);
                }

                vhdFile.Stream.Position = driveSize;

                var footer = new VhdFooter();
                footer.InitDefaults();
                footer.OrigSize = ((ulong) driveSize).EndianSwap();
                footer.CurSize = footer.OrigSize;
                footer.UniqueId = Header.VDUID;

                // don't need to calculate these
                footer.DiskGeometryHeads = 0;
                footer.DiskGeometrySectors = 0;
                footer.DiskGeometryCylinders = 0;
                footer.TimeStamp = 0;

                footer.CalculateChecksum();

                vhdFile.Writer.WriteStruct(footer);
            }
            if (DisableNativeFunctions)
                return true;

            // make sure NTFS compression is disabled on the vhd

            int lpBytesReturned = 0;
// ReSharper disable InconsistentNaming
            const int FSCTL_SET_COMPRESSION = 0x9C040;
            short COMPRESSION_FORMAT_NONE = 0;
// ReSharper restore InconsistentNaming

            using (FileStream f = File.Open(destFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
#pragma warning disable 618
                Natives.DeviceIoControl(f.Handle, FSCTL_SET_COMPRESSION,
#pragma warning restore 618
                    ref COMPRESSION_FORMAT_NONE, 2 /*sizeof(short)*/, IntPtr.Zero, 0,
                    ref lpBytesReturned, IntPtr.Zero);
            }

            return true;
        }

        public string GetXvcKey(int keyIndex, out byte[] keyOutput)
        {
            keyOutput = null;

            if (XvcInfo.EncryptionKeyIds == null || 
                XvcInfo.EncryptionKeyIds.Length <= keyIndex ||
                XvcInfo.KeyCount == 0 || 
                XvcInfo.EncryptionKeyIds[keyIndex].IsKeyNulled)
                return null;

            var keyGuid = new Guid(XvcInfo.EncryptionKeyIds[keyIndex].KeyId);

            if (XvcInfo.IsUsingTestCik && keyGuid == GetTestCikKey())
            {
                keyOutput = CikKeys[keyGuid];
                return "testsigned";
            }

            if (CikKeys.ContainsKey(keyGuid))
            {
                keyOutput = CikKeys[keyGuid];
                return keyGuid.ToString() + " (from cik_keys.bin)";
            }

            string licenseFolder = Path.GetDirectoryName(FilePath);
            if (Path.GetFileName(licenseFolder) == "MSXC")
                licenseFolder = Path.GetDirectoryName(licenseFolder);

            if (String.IsNullOrEmpty(licenseFolder))
                return null; // fix for weird resharper warning

            licenseFolder = Path.Combine(licenseFolder, "Licenses");

            if (!Directory.Exists(licenseFolder))
                return null;

            foreach (string file in Directory.GetFiles(licenseFolder, "*.xml"))
            {
                var xml = new XmlDocument();
                xml.Load(file);

                var xmlns = new XmlNamespaceManager(xml.NameTable);
                xmlns.AddNamespace("resp", "http://schemas.microsoft.com/xboxlive/security/clas/LicResp/v1");

                XmlNode licenseNode = xml.SelectSingleNode("//resp:SignedLicense", xmlns);
                if (licenseNode == null)
                    continue;

                string signedLicense = licenseNode.InnerText;
                byte[] signedLicenseBytes = Convert.FromBase64String(signedLicense);

                var licenseXml = new XmlDocument();
                licenseXml.LoadXml(Encoding.ASCII.GetString(signedLicenseBytes));

                var xmlns2 = new XmlNamespaceManager(licenseXml.NameTable);
                xmlns2.AddNamespace("resp", "http://schemas.microsoft.com/xboxlive/security/clas/LicResp/v1");

                XmlNode keyIdNode = licenseXml.SelectSingleNode("//resp:KeyId", xmlns2);
                if (keyIdNode == null)
                    continue;

                string keyId = keyIdNode.InnerText;

                if (keyId != keyGuid.ToString())
                    continue;

                XmlNode licenseBlockNode = licenseXml.SelectSingleNode("//resp:SPLicenseBlock", xmlns2);
                if (licenseBlockNode == null)
                    continue;

                string licenseBlock = licenseBlockNode.InnerText;
                byte[] licenseBlockBytes = Convert.FromBase64String(licenseBlock);

                var block = new XvcLicenseBlock(licenseBlockBytes);
                var keyIdBlock = block.GetBlockWithId(XvcLicenseBlockId.KeyId);
                if (keyIdBlock == null)
                    continue;

                if (!keyIdBlock.BlockData.IsEqualTo(XvcInfo.EncryptionKeyIds[keyIndex].KeyId))
                    continue;

                var decryptKeyBlock = block.GetBlockWithId(XvcLicenseBlockId.EncryptedCik);
                if (decryptKeyBlock == null)
                    continue;

                keyOutput = decryptKeyBlock.BlockData;

                // todo: decrypt/deobfuscate the key

                return file;
            }
            return null;
        }

        public byte[] Read(long offset, int count)
        {
            _io.Stream.Position = offset;
            return _io.Reader.ReadBytes(count);
        }

        #region ToString
        public override string ToString()
        {
            return ToString(false);
        }

        public string ToString(bool formatted)
        {
            var b = new StringBuilder();

            string fmt = formatted ? "    " : "";

            b.AppendLine("XvdMiscInfo:");
            b.AppendLineSpace(fmt + "Block Count: 0x" + XvdDataBlockCount.ToString("X"));

            if (Header.EmbeddedXVDLength > 0)
                b.AppendLineSpace(fmt + "Embedded XVD Offset: 0x3000");

            if(Header.UserDataLength > 0)
                b.AppendLineSpace(fmt + "User Data Offset: 0x" + UserDataOffset.ToString("X"));

            b.AppendLineSpace(fmt + "XVD Data Offset: 0x" + DataOffset.ToString("X"));

            if (IsDataIntegrityEnabled)
            {
                b.AppendLineSpace(fmt + "Hash Tree Block Count: 0x" + HashTreeBlockCount.ToString("X"));
                b.AppendLineSpace(fmt + "Hash Tree Levels: 0x" + HashTreeLevels.ToString("X"));
                b.AppendLineSpace(fmt + "Hash Tree Valid: " + (HashTreeValid ? "true" : "false"));

                if (!DisableDataHashChecking)
                    b.AppendLineSpace(fmt + "Data Hash Tree Valid: " + (DataHashTreeValid ? "true" : "false"));
            }

            if(IsXvcFile)
                b.AppendLineSpace(fmt + "XVC Data Hash Valid: " + (XvcDataHashValid ? "true" : "false"));

            b.AppendLine();
            b.Append(Header.ToString(formatted));

            if (XvcInfo.ContentID == null)
                return b.ToString();

            b.AppendLine();
            if (formatted)
            {
                byte[] decryptKey;
                string licenseFile = GetXvcKey(0, out decryptKey);
                if (!String.IsNullOrEmpty(licenseFile))
                {
                    if (licenseFile != "testsigned")
                        b.AppendLine("Decrypt key from license file " + licenseFile +
                                     " (key is wrong though until the obfuscation/encryption on it is figured out)");
                    else
                        b.AppendLine("Decrypt key for test-signed package:");

                    b.AppendLine(decryptKey.ToHexString());
                    b.AppendLine();
                }
            } 
            b.AppendLine(XvcInfo.ToString(formatted));

            if(RegionHeaders != null)
                for (int i = 0; i < RegionHeaders.Count; i++)
                {
                    b.AppendLine();
                    b.AppendLine("Region " + i);
                    b.Append(RegionHeaders[i].ToString(formatted));
                }

            if (RegionHeaders != null)
                for (int i = 0; i < UpdateSegments.Count; i++)
                {
                    if (UpdateSegments[i].Unknown1 == 0)
                        break;
                    b.AppendLine();
                    b.AppendLine("Update Segment " + i);
                    b.Append(UpdateSegments[i].ToString(formatted));
                }

            return b.ToString();
        }
        #endregion
    }
}
