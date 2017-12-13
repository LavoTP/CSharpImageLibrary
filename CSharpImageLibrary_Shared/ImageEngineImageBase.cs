﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CSharpImageLibrary.DDS;
using CSharpImageLibrary.Headers;
using static CSharpImageLibrary.ImageFormats;

namespace CSharpImageLibrary
{
    /// <summary>
    /// Represents an image. Can use Windows codecs if available.
    /// </summary>
    public abstract class ImageEngineImageBase<T> where T : MipMapBase, new()
    {
        /// <summary>
        /// Original file data used to create this image.
        /// </summary>
        public byte[] OriginalData = null;

        #region Properties
        /// <summary>
        /// Image header.
        /// </summary>
        public AbstractHeader Header { get; set; }

        /// <summary>
        /// Width of image.
        /// </summary>
        public int Width
        {
            get
            {
                return MipMaps[0].Width;
            }
        }

        /// <summary>
        /// Height of image.
        /// </summary>
        public int Height
        {
            get
            {
                return MipMaps[0].Height;
            }
        }

        /// <summary>
        /// Number of mipmaps present.
        /// </summary>
        public int NumMipMaps
        {
            get
            {
                return MipMaps.Count;
            }
        }

        /// <summary>
        /// Format of image.
        /// </summary>
        public ImageEngineFormat Format => FormatDetails.SurfaceFormat; 


        /// <summary>
        /// Contains details of the image format.
        /// </summary>
        public ImageEngineFormatDetails FormatDetails { get; protected set; }

        
        /// <summary>
        /// List of mipmaps. Single level images only have one mipmap.
        /// </summary>
        public List<MipMapBase> MipMaps { get; protected set; }

        /// <summary>
        /// Path to file. Null if no file e.g. thumbnail from memory.
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Size of Image when compressed (Essentially file size)
        /// </summary>
        public int CompressedSize { get; set; }


        /// <summary>
        /// Uncompressed size of main image (no mipmaps)
        /// </summary>
        public int UncompressedSize => FormatDetails.GetUncompressedSize(Width, Height, false);


        /// <summary>
        /// Number of channels in image. 
        /// NOTE: Still stored in memory as BGRA regardless.
        /// </summary>
        public int NumberOfChannels
        {
            get
            {
                return FormatDetails.MaxNumberOfChannels;
            }
        }

        /// <summary>
        /// Number of bits per colour.
        /// </summary>
        public int BitCount =>  FormatDetails.BitCount; 

        /// <summary>
        /// Number of bytes per colour. i.e. 1 byte, 4 bytes (int), etc
        /// </summary>
        public int ComponentSize => FormatDetails.ComponentSize; 

        /// <summary>
        /// True = Image is a block compressed DDS.
        /// </summary>
        public bool IsBlockCompressed => FormatDetails.IsBlockCompressed; 

        /// <summary>
        /// Size of compressed blocks. Affected by component size. Default is 1 for normal images (jpg, etc)
        /// </summary>
        public int BlockSize => FormatDetails.BlockSize;

        /// <summary>
        /// File Extension of selected format.
        /// </summary>
        public SupportedExtensions FileExtension => FormatDetails.Extension;
        #endregion Properties

        protected void LoadData(Stream stream)
        {
            CompressedSize = (int)stream.Length;
            Header = ImageEngine.LoadHeader(stream);
            FormatDetails = new ImageEngineFormatDetails(Header);
        }

        #region Savers
        public abstract Task Save(string destination, ImageEngineFormatDetails destFormatDetails, MipHandling GenerateMips, int desiredMaxDimension = 0, int mipToSave = 0, bool removeAlpha = true);

        public abstract Task Save(Stream destination, ImageEngineFormatDetails destFormatDetails, MipHandling GenerateMips, int desiredMaxDimension = 0, int mipToSave = 0, bool removeAlpha = true);
        
        public abstract Task<byte[]> Save(ImageEngineFormatDetails destFormatDetails, MipHandling GenerateMips, int desiredMaxDimension = 0, int mipToSave = 0, bool removeAlpha = true);

        protected async Task<byte[]> AttemptSaveUsingOriginalData(ImageEngineFormatDetails destFormatDetails, MipHandling GenerateMips, int desiredMaxDimension, int mipToSave, AlphaSettings alphaSetting)
        {

            // TODO This shouldb't be this complicated.
            // Should tidy this up too. Separate out some functions and reuse?



            int start = 0;
            int destStart = 0;
            int length = OriginalData.Length;
            int newWidth = Width;
            int newHeight = Height;
            DDS_Header tempHeader = null;
            byte[] data = null;
            byte[] tempOriginalData = OriginalData;

            if (destFormatDetails.IsDDS)
            {
                destStart = destFormatDetails.HeaderSize;
                start = destStart;

                int mipCount = 0;

                if (mipToSave != 0)
                {
                    mipCount = 1;
                    newWidth = MipMaps[mipToSave].Width;
                    newHeight = MipMaps[mipToSave].Height;

                    
                    start = destFormatDetails.GetCompressedSize(Width, Height, mipToSave);
                    length = destFormatDetails.GetCompressedSize(newWidth, newHeight, 1);
                }
                else if (desiredMaxDimension != 0 && desiredMaxDimension < Width && desiredMaxDimension < Height)
                {
                    int index = MipMaps.FindIndex(t => t.Width < desiredMaxDimension && t.Height < desiredMaxDimension);

                    // If none found, do a proper save and see what happens.
                    if (index == -1)
                        data = await Save(destFormatDetails, GenerateMips, desiredMaxDimension, mipToSave, alphaSetting == AlphaSettings.RemoveAlphaChannel);

                    mipCount -= index;
                    newWidth = MipMaps[index].Width;
                    newHeight = MipMaps[index].Height;

                    start = destFormatDetails.GetCompressedSize(Width, Height, index);
                    length = destFormatDetails.GetCompressedSize(newWidth, newHeight, mipCount);
                }
                else
                {
                    if (alphaSetting == AlphaSettings.RemoveAlphaChannel)
                    {
                        // Can't edit alpha directly in premultiplied formats. Not easily anyway.
                        if (destFormatDetails.IsPremultipliedFormat)
                            return null;  // Don't resave here.


                        // DDS Formats only
                        if (destFormatDetails.SupportsAlphaEditing)
                        {
                            tempOriginalData = new byte[OriginalData.Length];
                            Array.Copy(OriginalData, tempOriginalData, OriginalData.Length);

                            // Edit alpha values
                            int alphaStart = 128;
                            int alphaJump = 0;
                            byte[] alphaBlock = null;
                            if (destFormatDetails.IsBlockCompressed)
                            {
                                alphaJump = 16;
                                alphaBlock = new byte[8];
                                for (int i = 0; i < 8; i++)
                                    alphaBlock[i] = 255;
                            }
                            else
                            {
                                alphaJump = destFormatDetails.ComponentSize * 4;
                                alphaBlock = new byte[destFormatDetails.ComponentSize];

                                switch (destFormatDetails.ComponentSize)
                                {
                                    case 1:
                                        alphaBlock[0] = 255;
                                        break;
                                    case 2:
                                        alphaBlock = BitConverter.GetBytes(ushort.MaxValue);
                                        break;
                                    case 4:
                                        alphaBlock = BitConverter.GetBytes(1f);
                                        break;
                                }
                            }

                            for (int i = alphaStart; i < OriginalData.Length; i += alphaJump)
                                Array.Copy(alphaBlock, 0, tempOriginalData, i, alphaBlock.Length);
                        }
                    }


                    switch (GenerateMips)
                    {
                        case MipHandling.KeepExisting:
                            mipCount = NumMipMaps;
                            break;
                        case MipHandling.Default:
                            if (NumMipMaps > 1)
                                mipCount = NumMipMaps;
                            else
                                goto case MipHandling.GenerateNew;  // Eww goto...
                            break;
                        case MipHandling.GenerateNew:
                            ImageEngine.DestroyMipMaps(MipMaps);
                            ImageEngine.TestDDSMipSize(MipMaps, destFormatDetails, Width, Height, out double fixXScale, out double fixYScale, GenerateMips);

                            // Wrong sizing, so can't use original data anyway.
                            if (fixXScale != 0 || fixYScale != 0)
                                return null;


                            mipCount = DDSGeneral.BuildMipMaps(MipMaps);

                            // Compress mipmaps excl top - LEAVE THIS SAVE here, as it's just saving lower mips. Keeps the top one.
                            byte[] formattedMips = DDSSaving.Save(MipMaps.GetRange(1, MipMaps.Count - 1), destFormatDetails, alphaSetting);
                            if (formattedMips == null)
                                return null;

                            // Get top mip size and create destination array 
                            length = destFormatDetails.GetCompressedSize(newWidth, newHeight, 0);
                            data = new byte[formattedMips.Length + length];

                            // Copy smaller mips to destination
                            Array.Copy(formattedMips, destFormatDetails.HeaderSize, data, length, formattedMips.Length - destFormatDetails.HeaderSize);
                            break;
                        case MipHandling.KeepTopOnly:
                            mipCount = 1;
                            length = destFormatDetails.GetCompressedSize(newWidth, newHeight, 1);
                            break;
                    }
                }

                // Header
                tempHeader = new DDS_Header(mipCount, newHeight, newWidth, destFormatDetails);
            }
            
            // Use existing array, otherwise create one.
            data = data ?? new byte[length];
            Array.Copy(tempOriginalData, start, data, destStart, length - destStart);

            // Write header if existing (DDS Only)
            if (tempHeader != null)
                tempHeader.WriteToArray(data, 0);

            return data;
        }
        #endregion Savers

        /// <summary>
        /// Resizes image.
        /// If single mip, scales to DesiredDimension.
        /// If multiple mips, finds closest mip and scales it (if required). DESTROYS ALL OTHER MIPS.
        /// </summary>
        /// <param name="DesiredDimension">Desired size of images largest dimension.</param>
        public void Resize(int DesiredDimension)
        {
            var top = MipMaps[0];
            var determiningDimension = top.Width > top.Height ? top.Width : top.Height;
            double scale = (double)DesiredDimension / determiningDimension;  
            Resize(scale);
        }


        /// <summary>
        /// Scales top mipmap and DESTROYS ALL OTHERS.
        /// </summary>
        /// <param name="scale">Scaling factor. </param>
        public void Resize(double scale)
        {
            MipMapBase closestMip = null;
            double newScale = 0;
            double desiredSize = MipMaps[0].Width * scale;

            double min = double.MaxValue;
            foreach (var mip in MipMaps)
            {
                double temp = Math.Abs(mip.Width - desiredSize);
                if (temp < min)
                {
                    closestMip = mip;
                    min = temp;
                }
            }

            newScale = desiredSize / closestMip.Width;

            MipMaps[0] = closestMip.Resize(newScale);
            MipMaps.RemoveRange(1, NumMipMaps - 1);
        }
    }
}
