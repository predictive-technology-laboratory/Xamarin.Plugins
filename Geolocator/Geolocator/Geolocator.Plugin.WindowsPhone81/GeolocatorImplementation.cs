//
//  Copyright 2011-2013, Xamarin Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//

using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Plugin.Geolocator.Abstractions;
using System.Threading;

namespace Plugin.Geolocator
{
    /// <summary>
    /// Implementation for Geolocator
    /// </summary>
    public class GeolocatorImplementation : IGeolocator
    {
        public GeolocatorImplementation()
        {
            DesiredAccuracy = 100;
        }

        /// <inheritdoc/>
        public event EventHandler<PositionEventArgs> PositionChanged;

        /// <inheritdoc/>
        public event EventHandler<PositionErrorEventArgs> PositionError;

        /// <inheritdoc/>
        public bool SupportsHeading
        {
            get { return false; }
        }

        /// <inheritdoc/>
        public bool IsGeolocationAvailable
        {
            get
            {
                PositionStatus status = GetGeolocatorStatus();

                while (status == PositionStatus.Initializing)
                {
                    Task.Delay(10).Wait();
                    status = GetGeolocatorStatus();
                }

                return status != PositionStatus.NotAvailable;
            }
        }
        /// <inheritdoc/>
        public bool IsGeolocationEnabled
        {
            get
            {
                PositionStatus status = GetGeolocatorStatus();

                while (status == PositionStatus.Initializing)
                {
                    Task.Delay(10).Wait();
                    status = GetGeolocatorStatus();
                }

                return status != PositionStatus.Disabled && status != PositionStatus.NotAvailable;
            }
        }
        /// <inheritdoc/>
        public double DesiredAccuracy
        {
            get { return desiredAccuracy; }
            set
            {
                desiredAccuracy = value;
                GetGeolocator().DesiredAccuracy = (value < 100) ? PositionAccuracy.High : PositionAccuracy.Default;
            }
        }
        /// <inheritdoc/>
        public bool IsListening
        {
            get { return isListening; }
        }

        /// <inheritdoc/>
        public Task<Position> GetPositionAsync(int timeoutMilliseconds = Timeout.Infite, CancellationToken? token = null, bool includeHeading = false)
        {
            if (timeoutMilliseconds < 0 && timeoutMilliseconds != Timeout.Infite)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");

            if (!token.HasValue)
                token = CancellationToken.None;

            IAsyncOperation<Geoposition> pos = GetGeolocator().GetGeopositionAsync(TimeSpan.FromTicks(0), TimeSpan.FromDays(365));
            token.Value.Register(o => ((IAsyncOperation<Geoposition>)o).Cancel(), pos);


            var timer = new Timeout(timeoutMilliseconds, pos.Cancel);

            var tcs = new TaskCompletionSource<Position>();

            pos.Completed = (op, s) =>
            {
                timer.Cancel();

                switch (s)
                {
                    case AsyncStatus.Canceled:
                        tcs.SetCanceled();
                        break;
                    case AsyncStatus.Completed:
                        tcs.SetResult(GetPosition(op.GetResults()));
                        break;
                    case AsyncStatus.Error:
                        Exception ex = op.ErrorCode;
                        if (ex is UnauthorizedAccessException)
                            ex = new GeolocationException(GeolocationError.Unauthorized, ex);

                        tcs.SetException(ex);
                        break;
                }
            };

            return tcs.Task;
        }
        /// <inheritdoc/>
        public Task<bool> StartListeningAsync(int minTime, double minDistance, bool includeHeading = false, ListenerSettings settings = null)
        {
            if (minTime < 0)
                throw new ArgumentOutOfRangeException("minTime");
            if (minDistance < 0)
                throw new ArgumentOutOfRangeException("minDistance");
            if (isListening)
                throw new InvalidOperationException();

            isListening = true;

            var loc = GetGeolocator();
            loc.ReportInterval = (uint)minTime;
            loc.MovementThreshold = minDistance;
            loc.PositionChanged += OnLocatorPositionChanged;
            loc.StatusChanged += OnLocatorStatusChanged;

            return Task.FromResult(true);
        }
        /// <inheritdoc/>
        public Task<bool> StopListeningAsync()
        {
            if (!isListening)
                return Task.FromResult(true);

            locator.PositionChanged -= OnLocatorPositionChanged;
            locator.StatusChanged -= OnLocatorStatusChanged;
            isListening = false;

            return Task.FromResult(true);
        }

        private bool isListening;
        private double desiredAccuracy;
        private Windows.Devices.Geolocation.Geolocator locator = new Windows.Devices.Geolocation.Geolocator();

        private async void OnLocatorStatusChanged(Windows.Devices.Geolocation.Geolocator sender, StatusChangedEventArgs e)
        {
            GeolocationError error;
            switch (e.Status)
            {
                case PositionStatus.Disabled:
                    error = GeolocationError.Unauthorized;
                    break;

                case PositionStatus.NoData:
                    error = GeolocationError.PositionUnavailable;
                    break;

                default:
                    return;
            }

            if (isListening)
            {
                await StopListeningAsync();
                OnPositionError(new PositionErrorEventArgs(error));
            }

            locator = null;
        }

        private void OnLocatorPositionChanged(Windows.Devices.Geolocation.Geolocator sender, PositionChangedEventArgs e)
        {
            OnPositionChanged(new PositionEventArgs(GetPosition(e.Position)));
        }

        private void OnPositionChanged(PositionEventArgs e)
        {
            var handler = PositionChanged;
            if (handler != null)
                handler(this, e);
        }

        private void OnPositionError(PositionErrorEventArgs e)
        {
            var handler = PositionError;
            if (handler != null)
                handler(this, e);
        }

        private Windows.Devices.Geolocation.Geolocator GetGeolocator()
        {
            var loc = locator;
            if (loc == null)
            {
                locator = new Windows.Devices.Geolocation.Geolocator();
                locator.StatusChanged += OnLocatorStatusChanged;
                loc = locator;
            }

            return loc;
        }

        private PositionStatus GetGeolocatorStatus()
        {
            var loc = GetGeolocator();
            return loc.LocationStatus;
        }

        private static Position GetPosition(Geoposition position)
        {
            var pos = new Position
            {
                Accuracy = position.Coordinate.Accuracy,
                Latitude = position.Coordinate.Point.Position.Latitude,
                Longitude = position.Coordinate.Point.Position.Longitude,
                Timestamp = position.Coordinate.Timestamp.ToUniversalTime(),
            };

            if (position.Coordinate.Heading != null)
                pos.Heading = position.Coordinate.Heading.Value;

            if (position.Coordinate.Speed != null)
                pos.Speed = position.Coordinate.Speed.Value;

            if (position.Coordinate.AltitudeAccuracy.HasValue)
                pos.AltitudeAccuracy = position.Coordinate.AltitudeAccuracy.Value;

            pos.Altitude = position.Coordinate.Point.Position.Altitude;

            return pos;
        }
    }
}
