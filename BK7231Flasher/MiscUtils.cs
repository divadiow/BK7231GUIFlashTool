﻿using System;
using System.Collections.Generic;
using System.Text;

namespace BK7231Flasher
{
    public class MiscUtils
    {
        public static string formatDateNowFileNameBase()
        {
            string r = DateTime.Now.ToString("yyyy-dd-M--HH-mm-ss"); ;
            return r;
        }
        public static string formatDateNowFileName(string start, string ext)
        {
            string r = start + "_" + formatDateNowFileNameBase() + "." + ext;
            return r;
        }

        internal static byte[] padArray(byte[] data, int sector)
        {
            int rem = data.Length % sector;
            if (rem == 0)
                return data;
            int toAdd = sector - rem;
            byte[] ret = new byte[data.Length + toAdd];
            Array.Copy(data, 0, ret, 0, data.Length);
            for(int i = data.Length; i < ret.Length; i++)
            {
                ret[i] = 0xff;
            }
            return ret;
        }
        public static byte [] subArray(byte [] originalArray, int start, int length)
        {
            byte[] subArray = new byte[length];
            Array.Copy(originalArray, start, subArray, 0, length);
            return subArray;
        }
        public static int findFirst(byte[] dat, byte needle, int start)
        {
            for (int i = 0; start < dat.Length; i++)
            {
                if (dat[i] == needle)
                {
                    return i;
                }
            }
            return -1;
        }
        public static int indexOf(byte[] src, byte[] needle)
        {
            for (int i = 0; i < src.Length - needle.Length; i++)
            {
                bool bOk = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (src[i + j] != needle[j])
                    {
                        bOk = false;
                        break;
                    }
                }
                if (bOk)
                    return i;
            }
            return -1;
        }
    }
}
