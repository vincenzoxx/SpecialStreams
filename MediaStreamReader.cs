using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BECCore.Ranges;
using MoreLinq;
using SpecialStreams.Annotations;


namespace SpecialStreams
{

	public static class DelegateExtension
	{
		public static void SafeInvoke(this Delegate @delegate)
		{
			Task.Run(() =>
			{
				try
				{
					foreach (var asd in @delegate.GetInvocationList())
						asd.Method.Invoke(asd.Target, null);

				}
				catch
				{
				}
			});
		}
	}

	enum BufferOperationType
	{
		GetReadingPosition,
		AddRange,
	}

	class BufferOperation
	{
		public BufferOperationType BufferOperationType { get; }

		public BufferOperation(BufferOperationType bufferOperationType)
		{
			BufferOperationType = bufferOperationType;
		}
	}

	public delegate void AutoStreamReaderCompletedEventHandler();

	public delegate void AutoStreamReaderClosedEventHandler();

	public delegate void AutoStreamReaderInitializedEventHandler();

	public delegate void AutoStreamReaderSizeChangedEventHandler();

	public delegate void AutoStreamReaderUpdatedEventHandler();

	public class MediaStreamReader : Stream, INotifyPropertyChanged
	{
		private bool _serverIsMultiRequest;

		Stream _serverStream;

		private Func<int, int, string, byte[]> _streamFunc;
		private string _streamFuncId;

		private Func<byte[], byte[]> _decompressFunction;

		Stream _writingStream;
		Stream _readingStream;
		IIntervalCollection _bufferedRanges;

		public IIntervalCollection Buffer
		{
			get => _bufferedRanges;
		}

		private readonly object _lockRead = new object();
		private int _zeroResultCounter;
		IntervalCombiner<Range> _periodCombiner;
		IntervalGapCalculator<Range> _gapCalculator;
		public bool Closed { get; private set; }
		public bool Completed { get; private set; }
		public bool Initialized { get; private set; }
		public SemaphoreSlim SyncLock = new SemaphoreSlim(1,1);
		public event AutoStreamReaderClosedEventHandler ClosedEvent;
		public event AutoStreamReaderCompletedEventHandler CompletedEvent;
		public event AutoStreamReaderInitializedEventHandler InitializedEvent;
		public event AutoStreamReaderSizeChangedEventHandler LengthChangedEvent;
		public event AutoStreamReaderUpdatedEventHandler UpdatedEvent;


		private string _tempFileName;
		private int _lastCountRequest = 524288;
		private int _maxDownloadSpeed = 524288;
		private int _minDownloadSpeed = 32000;



		public MediaStreamReader(Stream stream, Func<byte[], byte[]> decompressFunc = null)
		{
			_serverStream = stream;
			_decompressFunction = decompressFunc;
			Initialize();
			this._writingStream.SetLength(stream.Length);
		}

		public MediaStreamReader(string id, Func<int, int, string, byte[]> streamFunc, long currentLength, Func<byte[], byte[]> decompressFunc = null)
		{
			_streamFuncId = id;
			_streamFunc = streamFunc;
			_decompressFunction = decompressFunc;
			Initialize();
			this._writingStream.SetLength(currentLength);
		}

		private void Initialize()
		{
			_tempFileName = Path.Combine(Path.GetTempPath(), Path.GetTempFileName());
			_writingStream = new FileStream(_tempFileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Delete | FileShare.ReadWrite);
			_readingStream = new FileStream(_tempFileName, FileMode.Open, FileAccess.Read, FileShare.Delete | FileShare.ReadWrite);
			_bufferedRanges = new IntervalCollection();
			OnPropertyChanged(nameof(Buffer));
			_periodCombiner = new IntervalCombiner<Range>();
			_gapCalculator = new IntervalGapCalculator<Range>();
			Initialized = true;
			this.InitializedEvent?.SafeInvoke();
			Task.Run(AutoRead);
		}

		private async Task AutoRead()
		{
			while (!Closed && !Completed)
			{
				this.ReadInternal(fromLoop: true);
				await Task.Delay(500);
			}
		}


		private void ReadInternal(int count = default, bool fromLoop = false)
		{
			count = !fromLoop ? count : _lastCountRequest;
			var position = !fromLoop ? Position : BufferController(() => GetReadPosition(Position));
			IInterval nextRequest = new Range((ulong)position, (ulong)(position + count));
			var request = BufferController(() => AdjustRequest(nextRequest));
			if (request == null)
				return;
			try
			{
				if(!_serverIsMultiRequest)
					Monitor.Enter(this);
				request = BufferController(() => AdjustRequest(request));
				request?.ForEach(c => this.ReadFromServer(c, fromLoop));
			}
			finally
			{
				if(!_serverIsMultiRequest)
					Monitor.Exit(this);
			}


		}

		private void ReadFromServer(IInterval timePeriod, bool fromLoop)
		{
			var start = timePeriod.Start;
			var countRead = timePeriod.End - start;
			byte[] buffer;
			int totalRead = 0;
			var sw = Stopwatch.StartNew();
			try
			{
				if (_streamFunc != null)
				{
					buffer = _streamFunc.Invoke((int)countRead, (int)start, this._streamFuncId);
					if (buffer != null)
						totalRead = buffer.Length;
				}
				else
				{
					buffer = new byte[countRead];

					if (this._serverStream.Position != (long)start)
						this._serverStream.Seek((long)start, SeekOrigin.Begin);
					while (totalRead < (int)countRead)
					{
						int read = this._serverStream.Read(buffer, totalRead, (int)countRead - totalRead);
						if (read == 0)
							break;
						totalRead += read;
					}
				}
			}
			catch
			{
				buffer = new byte[0];
				totalRead = 0;
			}
			finally
			{
				sw.Stop();
				TimeSpan time = sw.Elapsed;
				var bytePerMezzoSecondo = _lastCountRequest / time.TotalSeconds / 2;
				var downloadSpeed = (int)bytePerMezzoSecondo;
				int tempDownloadSpeed = Math.Max(downloadSpeed, _minDownloadSpeed);
				_lastCountRequest = Math.Min(tempDownloadSpeed, _maxDownloadSpeed);
			}



			if (totalRead == 0)
			{
				if (++_zeroResultCounter < 20 || this.Completed) return;
				this.Completed = true;
				this.CompletedEvent?.SafeInvoke();
				return;
			}
			if (fromLoop)
				_zeroResultCounter = 0;

			byte[] result = new byte[totalRead];
			Array.Copy(buffer, result, totalRead);
			var chunkDecompressed = this._decompressFunction?.Invoke(result) ?? result;
			this.BufferController(() =>
			{
				this.UpdateStream(chunkDecompressed, (long)start);
				return true;
			});
		}


		private long GetReadPosition(long position)
		{
			var nextTime = this._bufferedRanges.FirstOrDefault(c => c.HasInside((ulong)position)) ?? new Range((ulong)position);
			var next = nextTime.End;
			if (this.Length == (long)next && this._bufferedRanges.Count > 1 && _zeroResultCounter == 1)
				next = this._bufferedRanges.MinBy(c => c.End).End;
			return (long)next;
		}

		private void UpdateStream(byte[] chunk, long position)
		{
			if (Closed) return;
			var newPositionFromServer = chunk.Length + position;
			try
			{
				if (this._writingStream.Length < newPositionFromServer)
				{
					this._writingStream.SetLength(newPositionFromServer);
					LengthChangedEvent?.SafeInvoke();
					OnPropertyChanged(nameof(Length));
				}

				this._writingStream.Seek(position, SeekOrigin.Begin);
				this._writingStream.Write(chunk, 0, chunk.Length);
				this._writingStream.Flush();
				this.RebuildBufferedRanges(new Range((ulong)position, (ulong)newPositionFromServer));
				UpdatedEvent?.SafeInvoke();
			}
			catch (Exception ex)
			{
			}
		}

		private IIntervalCollection AdjustRequest(IInterval nextRequest)
		{
			if (_bufferedRanges.Any(c => c.HasInside(nextRequest)))
				return null;
			return this.IsNotIncluded(_bufferedRanges, nextRequest) || _bufferedRanges.IsAnytime ? new IntervalCollection(new[] {nextRequest}) : _gapCalculator.GetGaps(new IntervalCollection(_bufferedRanges.IntersectionPeriods(nextRequest).Cast<Range>().Select(c => c.GetIntersection(nextRequest))), nextRequest);
		}

		private bool IsNotIncluded(IIntervalCollection collection, IInterval period)
		{
			return collection.All(c => !c.IntersectsWith(period) && (c.GetRelation(period) == IntervalRelation.Before || c.GetRelation(period) == IntervalRelation.After));
		}
		private void RebuildBufferedRanges(IInterval newRange)
		{
			_bufferedRanges.Add(newRange);
			_bufferedRanges = _periodCombiner.CombineIntervals(_bufferedRanges);
			if (_bufferedRanges.Start != TimeSpec.MinPeriodDate)
				_bufferedRanges.Add(BuildFromPosition(0));
			Trace.WriteLine("Autostream");
			Trace.WriteLine("------------------------");
			foreach (Range bufferedRange in _bufferedRanges)
				Trace.WriteLine(bufferedRange.ToString());
			Trace.WriteLine("------------------------");
			OnPropertyChanged(nameof(Buffer));
		}

		private static IInterval BuildFromPosition(long position)
		{
			return new Range((ulong)position);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			lock (_lockRead)
			{
				this.ReadInternal(count);
				byte[] array = new byte[0];
				try
				{
					array = this.ReadExactly(count);
					Array.Copy(array, 0, buffer, offset, array.Length);
				}
				catch
				{
				}

				return array.Length;
			}
		}

		public byte[] Read(int count, ref int num)
		{
			lock (_lockRead)
			{
				this.ReadInternal(count);
				byte[] result = new byte[0];
				try
				{
					result = this.ReadExactly(count);
					num = result.Length;
				}
				catch
				{
				}

				return result;
			}

		}


		private byte[] ReadExactly(int count)
		{
			byte[] buffer = new byte[count];
			int offset = 0;
			while (offset < count)
			{
				int read = _readingStream.Read(buffer, offset, count - offset);
				if (read == 0)
					break;
				offset += read;
			}

			byte[] result = new byte[offset];
			Array.Copy(buffer, result, offset);
			return result;

		}

		private T BufferController<T>(Func<T> func)
		{
			lock (this)
				return func.Invoke();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return _readingStream.Seek(offset, origin);
		}

		public override void Flush()
		{
			if (!CanWrite) throw new NotImplementedException("This stream is for reading purpose only");
		}

		public override void SetLength(long value)
		{
			if (!CanWrite) throw new NotImplementedException("This stream is for reading purpose only");
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			if (!CanWrite)
				throw new NotImplementedException("This stream is for reading purpose only");
		}

		public override bool CanRead => _readingStream.CanRead;
		public override bool CanSeek => _readingStream.CanSeek;
		public override bool CanWrite => _readingStream.CanWrite;
		public override long Length => _readingStream.Length;

		public override long Position
		{
			get => this._readingStream.Position;
			set
			{
				lock(_lockRead)
					this._readingStream.Position = value;
			}
		}

		private void CloseStreams()
		{
			this.Closed = true;
			this.ClosedEvent?.SafeInvoke();
			_writingStream?.Close();
			_writingStream?.Dispose();
			_writingStream = null;
			_readingStream?.Close();
			_readingStream?.Dispose();
			_readingStream = null;
			if (File.Exists(_tempFileName))
				File.Delete(_tempFileName);
		}

		public override void Close()
		{
			lock (this)
			{
				try
				{
					this.CloseStreams();
					base.Close();
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine(ex);
				}
			}
		}

		#region INotifyPropertyChanged

		public event PropertyChangedEventHandler PropertyChanged;

		[NotifyPropertyChangedInvocator]
		protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		#endregion
	}
}
