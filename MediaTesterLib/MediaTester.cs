﻿using System;
using System.Diagnostics;
using System.IO;

namespace MediaTesterLib
{
	public delegate void WriteBlockCompleteHandler(MediaTester mediaTester, long absoluteDataBlockIndex, long absoluteDataByteIndex, string testFilePath, long writeBytesPerSecond, int bytesWritten, int bytesFailedWrite);
	public delegate void VerifyBlockCompleteHandler(MediaTester mediaTester, long absoluteDataBlockIndex, long absoluteDataByteIndex, string testFilePath, long readBytesPerSecond, int bytesVerified, int bytesFailed);
	public delegate void ExceptionHandler(MediaTester mediaTester, Exception exception);

	public class MediaTester
	{
		const FileOptions FileFlagNoBuffering = (FileOptions)0x20000000;
		public const int DATA_BLOCK_SIZE = 8 * 1024 * 1024;
		public const int DATA_BLOCKS_PER_FILE = 1 * 1024 / 8;
		public const int FILE_SIZE = DATA_BLOCK_SIZE * DATA_BLOCKS_PER_FILE; // 1 GB == 1024 * 1024 * 1024 == 1,073,741,824
		public const string TempSubDirectoryName = "MediaTester";

		public WriteBlockCompleteHandler AfterWriteBlock;
		public VerifyBlockCompleteHandler AfterQuickTest;
		public VerifyBlockCompleteHandler AfterVerifyBlock;
		public ExceptionHandler OnException;

		private bool _isBatchMode = false;
		private Options _options;
		public Options Options
		{
			get
			{
				if (_options == null)
				{
					_options = new Options();
				}
				return _options;
			}
			set
			{
				_options = value;
			}
		}

		public bool IsSuccess { get; protected set; } = true;

		private long GetTotalDataFileBytes()
		{
			long totalBytes = 0;
			for (int testFileIndex = 0; ; testFileIndex++)
			{
				string testFilePath = GetTestFilePath(testFileIndex);
				if (!File.Exists(testFilePath))
					break;

				FileInfo fileInfo = new FileInfo(testFilePath);
				totalBytes += fileInfo.Length;
			}

			return totalBytes;
		}

		public decimal ProgressPercent { get; protected set; }
		public long TotalBytesWritten { get; protected set; }
		public long TotalBytesVerified { get; protected set; }
		public long TotalBytesFailed { get; protected set; }
		public long TotalGeneratedTestFileBytes { get; protected set; }
		public decimal FirstFailingByteIndex { get; protected set; } = -1;

		public MediaTester(Options options)
		{
			Options = options;
		}

		public MediaTester(string testDirectory)
		{
			Options.TestDirectory = testDirectory;
		}

		public bool FullTest()
		{
			bool success;
			_isBatchMode = true;

			success = GenerateTestFiles();
			if (Options.StopProcessingOnFailure && !success && Options.QuickFirstFailingByteMethod)
				return success;

			success = VerifyTestFiles();
			if (Options.StopProcessingOnFailure && !success)
				return success;

			if (TotalGeneratedTestFileBytes != TotalBytesVerified)
			{
				success = false;
				IsSuccess &= false;
				OnException(this, new Exception($"Total bytes verified does not match total bytes written. Total Bytes Verified: {TotalBytesVerified.ToString("#,##0")} ; Total Bytes Written: {TotalGeneratedTestFileBytes.ToString("#,##0")}"));
			}
			if (Options.StopProcessingOnFailure && !success)
				return success;

			_isBatchMode = false;
			return IsSuccess;
		}

		public bool GenerateTestFiles()
		{
			IsSuccess = true;
			TotalGeneratedTestFileBytes = 0;
			try
			{
				if (Options.TotalBytesToTest < 0)
				{
					Options.TotalBytesToTest = GetAvailableBytes();
				}

				int lastFileIndex = (int)((Options.TotalBytesToTest + FILE_SIZE - 1) / FILE_SIZE) - 1;
				for (int testFileIndex = 0; testFileIndex <= lastFileIndex; testFileIndex++)
				{
					int testFileSize = (testFileIndex == lastFileIndex) ? (int)(Options.TotalBytesToTest % FILE_SIZE) : FILE_SIZE;
					if (testFileSize == 0)
						testFileSize = FILE_SIZE;

					int actualTestFileSize;
					string testFilePath = GenerateTestFile(testFileIndex, testFileSize, out actualTestFileSize);
					TotalGeneratedTestFileBytes += actualTestFileSize;
					if (Options.QuickTestAfterEachFile && testFilePath != null)
					{
						int checkIndex = GetLastDataBlockIndex(actualTestFileSize);
						long absoluteDataBlockIndex = GetAbsoluteDataBlockIndex(testFileIndex, checkIndex);
						long absoluteDataByteIndex = GetAbsoluteDataByteIndex(testFileIndex, checkIndex);
						int bytesVerified, bytesFailed;
						long readBytesPerSecond;
						SetProgressPercent((100M * ((decimal)absoluteDataByteIndex + (decimal)DATA_BLOCK_SIZE)) / (decimal)Options.TotalBytesToTest, 1);
						bool success = VerifyTestFileDataBlock(testFileIndex, testFilePath, checkIndex, out bytesVerified, out bytesFailed, out readBytesPerSecond);
						IsSuccess &= success;
						AfterQuickTest?.Invoke(this, absoluteDataBlockIndex, absoluteDataByteIndex, testFilePath, readBytesPerSecond, bytesVerified, bytesFailed);
						if (bytesFailed > 0 && Options.QuickFirstFailingByteMethod)
						{
							success = false;
							VerifyTestFile(testFileIndex, testFilePath, out bytesVerified, out bytesFailed);
						}

						if (Options.StopProcessingOnFailure && !success)
							return success;
					}
				}

				return IsSuccess;
			}
			catch (Exception ex)
			{
				IsSuccess = false;
				OnException?.Invoke(this, new Exception("An unhandled exception ocurred while writing test data.", ex));
				return false;
			}
		}

		private string GenerateTestFile(int testFileIndex, int testFileSize, out int finalTestFileSize)
		{
			long freeSpace = GetAvailableBytes();
			finalTestFileSize = testFileSize;
			if (finalTestFileSize > freeSpace)
				finalTestFileSize = (int)freeSpace;

			if (finalTestFileSize == 0)
				return null;

			string testFilePath = GetTestFilePath(testFileIndex);
			Directory.CreateDirectory(GetTestDirectory());

			if (File.Exists(testFilePath))
			{
				var fileInfo = new FileInfo(testFilePath);
				if (finalTestFileSize <= fileInfo.Length && fileInfo.Length <= FILE_SIZE)
				{
					// File already exists. Leaving in place.
					TotalBytesWritten += finalTestFileSize;
					return testFilePath;
				}
			}

			try
			{
				// Create file and write
				using (var file = new FileStream(testFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, DATA_BLOCK_SIZE,
					FileFlagNoBuffering | FileOptions.WriteThrough))
				using (var fileWriter = new BinaryWriter(file))
				{
					int lastDataBlockIndex = GetLastDataBlockIndex(finalTestFileSize);
					for (int dataBlockIndex = 0; dataBlockIndex <= lastDataBlockIndex; dataBlockIndex++)
					{
						int dataBlockSize = (dataBlockIndex == lastDataBlockIndex && finalTestFileSize % DATA_BLOCK_SIZE != 0)
							? (int)(finalTestFileSize % DATA_BLOCK_SIZE) : DATA_BLOCK_SIZE;
						long absoluteDataBlockIndex = GetAbsoluteDataBlockIndex(testFileIndex, dataBlockIndex);
						long absoluteDataByteIndex = GetAbsoluteDataByteIndex(testFileIndex, dataBlockIndex);

						try
						{
							var dataBlock = GenerateDataBlock(testFileIndex, dataBlockIndex, dataBlockSize);

							var stopwatch = new Stopwatch();
							stopwatch.Start();
							fileWriter.Write(dataBlock);
							stopwatch.Stop();
							long writeBytesPerSecond = (long)((decimal)dataBlockSize / ((decimal)stopwatch.Elapsed.Ticks / (decimal)TimeSpan.TicksPerMillisecond / 1000M));
							TotalBytesWritten += dataBlockSize;
							SetProgressPercent((100M * ((decimal)absoluteDataByteIndex + (decimal)dataBlockSize)) / (decimal)Options.TotalBytesToTest, 1);
							AfterWriteBlock?.Invoke(this, absoluteDataBlockIndex, absoluteDataByteIndex, testFilePath, writeBytesPerSecond, dataBlockSize, 0);
						}
						catch (Exception ex)
						{
							IsSuccess = false;
							AfterWriteBlock?.Invoke(this, absoluteDataBlockIndex, absoluteDataByteIndex, testFilePath, 0, 0, dataBlockSize);
							OnException?.Invoke(this, new Exception($"Unable to write block to file '{testFilePath}'.", ex));
							return testFilePath;
						}
					}
				}
			}
			catch (Exception ex)
			{
				testFilePath = null;
				IsSuccess = false;
				OnException?.Invoke(this, new Exception($"Unable to open file '{testFilePath}' for writing.", ex));
			}

			return testFilePath;
		}

		public string GetTestDirectory()
		{
			string testDirectory = Options.TestDirectory;
			if (testDirectory.EndsWith(":"))
			{
				testDirectory += "\\";
			}

			if (!testDirectory.TrimEnd('\\').EndsWith(TempSubDirectoryName))
			{
				testDirectory = Path.Combine(testDirectory, TempSubDirectoryName);
			}

			return Path.Combine(testDirectory);
		}

		static public long GetAvailableBytes(string directory)
		{
			long totalSize;
			return GetAvailableBytes(directory, out totalSize);
		}

		static public long GetAvailableBytes(string directory, out long totalSize)
		{
			var driveInfo = new DriveInfo(directory);
			totalSize = driveInfo.TotalSize;
			long freeSpace = driveInfo.AvailableFreeSpace;
			int skippedFiles = 0;

			// Add the space taken by existing TestMedia files
			for (int testFileIndex = 0; skippedFiles < 100; testFileIndex++)
			{
				string testFilePath = GetTestFilePath(directory, testFileIndex);
				if (!File.Exists(testFilePath))
				{
					skippedFiles++;
					continue;
				}
				var fileInfo = new FileInfo(testFilePath);

				freeSpace += fileInfo.Length;
			}

			return freeSpace;
		}
		private long GetAvailableBytes()
		{
			long totalSize;
			return GetAvailableBytes(GetTestDirectory(), out totalSize);
		}

		private int GetLastDataBlockIndex(int testFileSize)
		{
			return ((testFileSize + DATA_BLOCK_SIZE - 1) / DATA_BLOCK_SIZE) - 1;
		}

		public bool VerifyTestFiles()
		{
			TotalBytesVerified = 0;
			TotalBytesFailed = 0;

			if (!_isBatchMode)
				IsSuccess = true;

			bool allFilesSuccess = true;
			if (Options.TotalBytesToTest < 0)
				Options.TotalBytesToTest = GetTotalDataFileBytes();

			for (int testFileIndex = 0; ; testFileIndex++)
			{
				string testFilePath = GetTestFilePath(testFileIndex);
				if (!File.Exists(testFilePath))
					break;

				int bytesVerified, bytesFailed;
				bool success = VerifyTestFile(testFileIndex, testFilePath, out bytesVerified, out bytesFailed, true);
				allFilesSuccess &= success;
				IsSuccess &= success;

				if (Options.StopProcessingOnFailure && !success)
					return success;

				if (TotalBytesVerified + TotalBytesFailed >= Options.TotalBytesToTest)
					return success; // The requested number of bytes has been verified
			}

			return allFilesSuccess;
		}

		private bool VerifyTestFile(int testFileIndex, string testFilePath, out int bytesVerified, out int bytesFailed, bool updateTotalBytes = false)
		{
			bool success = true;
			bytesVerified = 0;
			bytesFailed = 0;

			try
			{
				using (var fileReader = new FileStream(testFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, DATA_BLOCK_SIZE,
					FileFlagNoBuffering | FileOptions.SequentialScan))
				{
					int lastDataBlockIndex = GetLastDataBlockIndex((int)fileReader.Length);
					for (int dataBlockIndex = 0; dataBlockIndex <= lastDataBlockIndex; dataBlockIndex++)
					{
						long absoluteDataBlockIndex = GetAbsoluteDataBlockIndex(testFileIndex, dataBlockIndex);
						long absoluteDataByteIndex = GetAbsoluteDataByteIndex(testFileIndex, dataBlockIndex);
						long readBytesPerSecond;
						int blockBytesVerified, blockBytesFailed;
						try
						{
							bool blockSuccess = VerifyTestFileDataBlock(fileReader, testFileIndex, dataBlockIndex, out blockBytesVerified, out blockBytesFailed, out readBytesPerSecond);
							success &= blockSuccess;
							IsSuccess &= success;
							bytesVerified += blockBytesVerified;
							bytesFailed += blockBytesFailed;
							if (updateTotalBytes)
							{
								TotalBytesVerified += blockBytesVerified;
								TotalBytesFailed += blockBytesFailed;
							}

							int dataBlockSize = blockBytesVerified + blockBytesFailed;
							SetProgressPercent((100M * ((decimal)absoluteDataByteIndex + (decimal)dataBlockSize)) / (decimal)Options.TotalBytesToTest, 2);
							AfterVerifyBlock?.Invoke(this, absoluteDataBlockIndex, absoluteDataByteIndex, testFilePath, readBytesPerSecond, blockBytesVerified, blockBytesFailed);
						}
						catch (Exception ex)
						{
							success = false;
							IsSuccess &= success;
							long exceptionBlockBytesFailed = fileReader.Length - (dataBlockIndex * DATA_BLOCK_SIZE);
							if (exceptionBlockBytesFailed > DATA_BLOCK_SIZE)
								exceptionBlockBytesFailed = DATA_BLOCK_SIZE;

							AfterVerifyBlock?.Invoke(this, absoluteDataBlockIndex, absoluteDataByteIndex, testFilePath, 0, 0, (int)exceptionBlockBytesFailed);
						}

						if (Options.StopProcessingOnFailure && !success)
							return success;

						if (TotalBytesVerified + TotalBytesFailed + bytesVerified + bytesFailed >= Options.TotalBytesToTest)
							return success; // The requested number of bytes has been verified
					}
				}
			}
			catch (Exception ex)
			{
				OnException?.Invoke(this, new Exception($"Unable to open '{testFilePath}' for reading.", ex));
				success = false;
				IsSuccess &= success;
			}
			return success;
		}

		private const int BatchPhaseCount = 2;
		private void SetProgressPercent(decimal percent, int batchPhaseNumber)
		{
			if (_isBatchMode)
				percent = (percent / (decimal)BatchPhaseCount) + (100M * (decimal)(batchPhaseNumber - 1) / (decimal)BatchPhaseCount);

			if (percent < 0)
				percent = 0;
			else if (percent > 100)
				percent = 100;

			ProgressPercent = percent;
		}

		private bool VerifyTestFileDataBlock(int fileIndex, int dataBlockIndex, out int bytesVerified, out int bytesFailed, out long readBytesPerSecond)
		{
			return VerifyTestFileDataBlock(fileIndex, GetTestFilePath(fileIndex), dataBlockIndex, out bytesVerified, out bytesFailed, out readBytesPerSecond);
		}

		private bool VerifyTestFileDataBlock(int testFileIndex, string testFilePath, int dataBlockIndex, out int bytesVerified, out int bytesFailed, out long readBytesPerSecond)
		{
			using (var file = File.OpenRead(testFilePath))
			{
				return VerifyTestFileDataBlock(file, testFileIndex, dataBlockIndex, out bytesVerified, out bytesFailed, out readBytesPerSecond);
			}
		}

		private bool VerifyTestFileDataBlock(FileStream fileReader, int fileIndex, int dataBlockIndex, out int bytesVerified, out int bytesFailed, out long readBytesPerSecond)
		{
			var dataBlock = ReadDataBlock(fileReader, dataBlockIndex, out readBytesPerSecond);
			return VerifyDataBlock(dataBlock, fileIndex, dataBlockIndex, out bytesVerified, out bytesFailed);
		}

		private byte[] ReadDataBlock(FileStream fileReader, int dataBlockIndex, out long readBytesPerSecond)
		{
			int dataBlockStartIndex = dataBlockIndex * DATA_BLOCK_SIZE;
			long dataBlockSize = fileReader.Length - dataBlockStartIndex;
			if (dataBlockSize > DATA_BLOCK_SIZE)
				dataBlockSize = DATA_BLOCK_SIZE;

			var dataBlock = new byte[(int)dataBlockSize];
			if (fileReader.Position != dataBlockStartIndex)
				fileReader.Seek(dataBlockStartIndex, SeekOrigin.Begin);

			var stopwatch = new Stopwatch();
			stopwatch.Start();
			int readBytes = fileReader.Read(dataBlock, 0, dataBlock.Length);
			stopwatch.Stop();
			readBytesPerSecond = (long)((decimal)readBytes / ((decimal)stopwatch.Elapsed.Ticks / (decimal)TimeSpan.TicksPerMillisecond / 1000M));

			if (readBytes != dataBlock.Length)
			{
				Array.Resize(ref dataBlock, readBytes);
			}

			return dataBlock;
		}

		private bool VerifyDataBlock(byte[] dataBlock, int fileIndex, int dataBlockIndex, out int bytesVerified, out int bytesFailed)
		{
			bytesVerified = 0;
			bytesFailed = 0;
			var knownGoodDataBlock = GenerateDataBlock(fileIndex, dataBlockIndex, dataBlock.Length);
			if (dataBlock.Length > knownGoodDataBlock.Length)
			{
				bytesFailed = dataBlock.Length;
				SetFirstFailingByteIndex(GetAbsoluteDataByteIndex(fileIndex, dataBlockIndex));
				return false;
			}

			for (int i = 0; i < dataBlock.Length; i++)
			{
				if (dataBlock[i] == knownGoodDataBlock[i])
				{
					bytesVerified++;
				}
				else
				{
					bytesFailed++;
					SetFirstFailingByteIndex(GetAbsoluteDataByteIndex(fileIndex, dataBlockIndex) + i);
				}
			}

			return bytesFailed == 0;
		}

		private void SetFirstFailingByteIndex(long failingByteIndex)
		{
			if (FirstFailingByteIndex > failingByteIndex || FirstFailingByteIndex < 0)
			{
				FirstFailingByteIndex = failingByteIndex;
			}
		}

		private string GetTestFilePath(int fileIndex)
		{
			return GetTestFilePath(GetTestDirectory(), fileIndex);
		}

		static private string GetTestFilePath(string testDirectory, int fileIndex)
		{
			return Path.Combine(testDirectory, (fileIndex + 1).ToString("D8") + ".MediaTester");
		}

		private static long GetAbsoluteDataBlockIndex(int fileIndex, int fileDataBlockIndex)
		{
			return (long)fileIndex * (long)DATA_BLOCKS_PER_FILE + (long)fileDataBlockIndex;
		}

		private static long GetAbsoluteDataByteIndex(int fileIndex, int fileDataBlockIndex)
		{
			return GetAbsoluteDataBlockIndex(fileIndex, fileDataBlockIndex) * DATA_BLOCK_SIZE;
		}

		private static byte[] GenerateDataBlock(int fileIndex, int fileDataBlockIndex, int blockSize)
		{
			return GenerateDataBlock(GetAbsoluteDataBlockIndex(fileIndex, fileDataBlockIndex), blockSize);
		}

		private static byte[] GenerateDataBlock(long absoluteDataBlockIndex, int blockSize)
		{
			var dataBlock = new byte[blockSize];
			var rand = new Random(GetDataBlockSeed(absoluteDataBlockIndex));
			rand.NextBytes(dataBlock);
			return dataBlock;
		}

		private static int GetDataBlockSeed(long absoluteDataBlockIndex)
		{
			return (int)absoluteDataBlockIndex;
		}
	}
}
