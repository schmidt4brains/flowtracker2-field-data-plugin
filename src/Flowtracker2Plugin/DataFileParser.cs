﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FieldDataPluginFramework;
using FieldDataPluginFramework.Context;
using FieldDataPluginFramework.DataModel;
using FieldDataPluginFramework.DataModel.ChannelMeasurements;
using FieldDataPluginFramework.DataModel.DischargeActivities;
using FieldDataPluginFramework.DataModel.Meters;
using FieldDataPluginFramework.DataModel.Readings;
using FieldDataPluginFramework.DataModel.Verticals;
using FieldDataPluginFramework.Results;
using FieldDataPluginFramework.Units;
using ICSharpCode.SharpZipLib.Zip;
using SonTek.Framework.Configuration;
using SonTek.Framework.Data;
using SonTek.Globals.Common;

namespace FlowTracker2Plugin
{
    public class DataFileParser
    {
        private readonly ILog _log;
        private readonly IFieldDataResultsAppender _resultsAppender;

        public DataFileParser(ILog log, IFieldDataResultsAppender resultsAppender)
        {
            _log = log;
            _resultsAppender = resultsAppender;
        }

        public ParseFileResult Parse(Stream stream)
        {
            return Parse(stream, null);
        }

        private DataFile DataFile { get; set; }
        private UnitSystem UnitSystem { get; set; }

        public ParseFileResult Parse(Stream stream, LocationInfo locationInfo)
        {
            DataFile = GetDataFile(stream);

            if (DataFile == null)
                return ParseFileResult.CannotParse();

            if (locationInfo == null)
            {
                locationInfo = _resultsAppender.GetLocationByIdentifier(DataFile.Properties.SiteNumber);
            }

            return AppendResults(locationInfo);
        }

        private DataFile GetDataFile(Stream stream)
        {
            try
            {
                // The file content stream provided by the framework is not a file stream.
                // The SonTek framework can only parse files from disk
                // So copy the stream to a temporary file
                using (var tempFile = new TempFile())
                {
                    var tempPath = tempFile.ToString();
                    var byteCount = (int) stream.Length;

                    using (var reader = new BinaryReader(stream))
                    using (var writer = new BinaryWriter(new FileStream(tempPath, FileMode.Create)))
                    {
                        writer.Write(reader.ReadBytes(byteCount));
                        writer.Close();

                        var dataFile = new DataFileComplete(tempPath).GetDataFile();

                        _log.Info($"Loaded {dataFile.Configuration.DataCollectionMode}.{dataFile.Configuration.Discharge.DischargeEquation} measurement from {dataFile.HandheldInfo.SerialNumber}/{dataFile.HandheldInfo.CpuSerialNumber}/{dataFile.HandheldInfo.SoftwareVersion}");

                        return dataFile;
                    }
                }
            }
            catch (ZipException e)
            {
                // We quickly land here if a non-ZIP file is parsed
                LogException("ZipException", e);
                return null;
            }
            catch (IOException e)
            {
                // We quickly land here if a ZIP archive is parsed, but it doesn't contain the expected FlowTracker2 JSON content.
                LogException("IOException", e);
                return null;
            }
        }

        private void LogException(string message, Exception exception)
        {
            _log.Error($"{message}: {exception.Message}\n{exception.StackTrace}");

            if (exception.InnerException != null)
            {
               LogException("InnerException", exception.InnerException); 
            }
        }

        private ParseFileResult AppendResults(LocationInfo locationInfo)
        {
            try
            {
                UnitSystem = CreateUnitSystem();

                var visit = CreateVisit(locationInfo);

                var dischargeActivity = CreateDischargeActivity(visit);

                AddGageHeightMeasurement(dischargeActivity);

                var manualGauging = CreateManualGauging(dischargeActivity);

                var startStationType = DataFile.Stations.First().StationType;
                var endStationType = DataFile.Stations.Last().StationType;

                foreach (var station in DataFile.Stations)
                {
                    manualGauging.Verticals.Add(CreateVertical(station, startStationType, endStationType));
                }

                _resultsAppender.AddDischargeActivity(visit, dischargeActivity);

                AddTemperatureReadings(visit);

                return ParseFileResult.SuccessfullyParsedAndDataValid();
            }
            catch (Exception exception)
            {
                LogException("Parsing error", exception);
                return ParseFileResult.SuccessfullyParsedButDataInvalid(exception);
            }
        }

        private UnitSystem CreateUnitSystem()
        {
            // This is a bit odd. The sample data file I've seen has "Units" = "English", yet is still using metric units everywhere
            var isMetric = true; // dataFile.HandheldInfo.Settings.GetEnum<UnitType>("Units") == UnitType.Metric;

            // The Metric and Imperial unit IDs listed here are stock in every AQTS system and cannot be deleted.
            // It is safe to hard-code these unit IDs

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            return isMetric
                ? new UnitSystem
                {
                    DistanceUnitId = "m",
                    AreaUnitId = "m^2",
                    VelocityUnitId = "m/s",
                    DischargeUnitId = "m^3/s",
                }
                : new UnitSystem
                {
                    DistanceUnitId = "ft",
                    AreaUnitId = "ft^2",
                    VelocityUnitId = "ft/s",
                    DischargeUnitId = "ft^3/s",
                };
        }

        private FieldVisitInfo CreateVisit(LocationInfo locationInfo)
        {
            var fieldVisitPeriod = new DateTimeInterval(DataFile.Properties.StartTime, DataFile.Properties.EndTime);
            var visitDetails = new FieldVisitDetails(fieldVisitPeriod)
            {
                Party = DataFile.Properties.Operator
            };

            return _resultsAppender.AddFieldVisit(locationInfo, visitDetails);
        }

        private DischargeActivity CreateDischargeActivity(FieldVisitInfo visit)
        {
            var dischargeActivityFactory = new DischargeActivityFactory(UnitSystem)
            {
                DefaultParty = DataFile.Properties.Operator
            };

            var dischargeActivity = dischargeActivityFactory.CreateDischargeActivity(
                new DateTimeInterval(visit.StartDate, visit.EndDate), DataFile.Calculations.Discharge);
            dischargeActivity.Comments = DataFile.Properties.Comment;

            return dischargeActivity;
        }

        private void AddTemperatureReadings(FieldVisitInfo visit)
        {
            const string waterTemperatureParameterId = "TW";
            const string degreesCelciusUnitId = "degC";

            var visitDuration = visit.EndDate - visit.StartDate;
            var midVisitTime = visit.StartDate + TimeSpan.FromTicks(visitDuration.Ticks / 2);

            var temperatureReading = new Reading(waterTemperatureParameterId,
                    new Measurement(DataFile.Calculations.Temperature, degreesCelciusUnitId))
            {
                MeasurementDevice = new MeasurementDevice("SonTek", "ProbeModel", "ProbeSerial"),
                DateTimeOffset = midVisitTime
            };

            _resultsAppender.AddReading(visit, temperatureReading);
        }

        private ManualGaugingDischargeSection CreateManualGauging(DischargeActivity dischargeActivity)
        {
            var manualGaugingDischargeSectionFactory = new ManualGaugingDischargeSectionFactory(UnitSystem);

            var manualGauging =
                manualGaugingDischargeSectionFactory.CreateManualGaugingDischargeSection(
                    dischargeActivity.MeasurementPeriod, dischargeActivity.Discharge.Value);

            manualGauging.AreaValue = DataFile.Calculations.Area;
            manualGauging.WidthValue = DataFile.Calculations.Width;
            manualGauging.VelocityAverageValue = DataFile.Calculations.Velocity.X;
            manualGauging.StartPoint = DataFile.Stations.First().StationType == StationType.RightBank
                ? StartPointType.RightEdgeOfWater
                : StartPointType.LeftEdgeOfWater;
            manualGauging.VelocityObservationMethod = FindMostCommonVelocityMethod();
            manualGauging.DischargeMethod = CreateDischargeMethodType();

            dischargeActivity.ChannelMeasurements.Add(manualGauging);

            return manualGauging;
        }

        private void AddGageHeightMeasurement(DischargeActivity dischargeActivity)
        {
            var gageHeight = DataFile.Calculations.GaugeHeight;

            if (double.IsNaN(gageHeight))
            {
                return;
            }

            dischargeActivity.GageHeightMeasurements.Add(
                new GageHeightMeasurement(new Measurement(gageHeight, UnitSystem.DistanceUnitId)));
        }

        private DischargeMethodType CreateDischargeMethodType()
        {
            var dischargeEquation = DataFile.Configuration.Discharge.DischargeEquation;

            switch (dischargeEquation)
            {
                case DischargeEquation.MeanSection:
                    return DischargeMethodType.MeanSection;

                case DischargeEquation.MidSection:
                    return DischargeMethodType.MidSection;
            }

            throw new ArgumentException($"DischargeEquation='{dischargeEquation}' is not supported");
        }

        private Vertical CreateVertical(Station station, StationType startStationType, StationType endStationType)
        {
            // TODO: Need to think about island measurements
            var verticalType = station.StationType == startStationType
                ? VerticalType.StartEdgeNoWaterBefore
                : station.StationType == endStationType
                    ? VerticalType.EndEdgeNoWaterAfter
                    : VerticalType.MidRiver;

            var vertical = new Vertical
            {
                TaglinePosition = station.Location,
                Comments = station.Comment,
                MeasurementTime = station.CreationTime,
                EffectiveDepth = station.GetEffectiveDepth(),
                SoundedDepth = station.GetFinalDepth(),
                MeasurementConditionData = CreateMeasurementCondition(station),
                VelocityObservation = new VelocityObservation
                {
                    VelocityObservationMethod = GetPointVelocityObservationType(station.VelocityMethod),
                    MeterCalibration = CreateMeterCalibration(station),
                    MeanVelocity = station.Calculations.MeanVelocityInVertical.X, // TODO: Is this MidSection vs MeanSection
                    DeploymentMethod = DeploymentMethodType.Unspecified,
                },
                FlowDirection = FlowDirectionType.Normal,
                VerticalType = verticalType,
                Segment = new Segment
                {
                    Width = station.Calculations.Width,
                    Area = station.Calculations.Area,
                    Discharge = station.Calculations.Discharge,
                    TotalDischargePortion = 100 * station.Calculations.FractionOfTotalDischarge,
                    Velocity = station.Calculations.MeanVelocityInVertical.X
                }
            };

            foreach (var pointMeasurement in station.PointMeasurements)
            {
                vertical.VelocityObservation.Observations.Add(new VelocityDepthObservation
                {
                    Depth = pointMeasurement.FractionalDepth * vertical.EffectiveDepth,
                    Velocity = pointMeasurement.Calculations.Velocity.X,
                    ObservationInterval = (pointMeasurement.EndTime - pointMeasurement.StartTime).TotalSeconds,
                    RevolutionCount = 0
                });
            }

            if (!vertical.VelocityObservation.Observations.Any())
            {
                // TODO: Make sure this match works out
                vertical.VelocityObservation.VelocityObservationMethod = PointVelocityObservationType.Surface;
                vertical.VelocityObservation.Observations.Add(new VelocityDepthObservation
                {
                    Depth = 0,
                    Velocity = 0,
                    ObservationInterval = 0,
                    RevolutionCount = 0
                });
            }

            return vertical;
        }

        private MeasurementConditionData CreateMeasurementCondition(Station station)
        {
            if (station.StationType == StationType.Ice)
                return new IceCoveredData
                {
                    WaterSurfaceToBottomOfIce = station.WaterSurfaceToBottomOfIce,
                    WaterSurfaceToBottomOfSlush = station.WaterSurfaceToBottomOfSlush,
                    IceThickness = station.IceThickness
                };

            return new OpenWaterData();
        }

        private MeterCalibration CreateMeterCalibration(Station station)
        {
            var point = station.PointMeasurements.FirstOrDefault();

            var meterCalibration = new MeterCalibration
            {
                MeterType = MeterType.Adv,
                Manufacturer = "SonTek",
                Model = "FlowTracker2",
                Configuration = $"{DataFile.HandheldInfo.SerialNumber}/{DataFile.HandheldInfo.CpuSerialNumber}",
                SoftwareVersion = point?.HandheldInfo.SoftwareVersion,
                FirmwareVersion = point?.ProbeInfo.FirmwareVersion,
                SerialNumber = point?.ProbeInfo.SerialNumber ?? DataFile.HandheldInfo.SerialNumber,
            };

            meterCalibration.Equations.Add(new MeterCalibrationEquation
            {
                InterceptUnitId = UnitSystem.DistanceUnitId
            });

            return meterCalibration;
        }

        private PointVelocityObservationType FindMostCommonVelocityMethod()
        {
            var velocityMethodCounts = new Dictionary<VelocityMethod, int>();

            foreach (var velocityMethod in DataFile.Stations.Select(station => station.VelocityMethod))
            {
                if (velocityMethodCounts.ContainsKey(velocityMethod))
                {
                    velocityMethodCounts[velocityMethod] += 1;
                }
                else
                {
                    velocityMethodCounts[velocityMethod] = 1;
                }
            }

            var maxVelocityMethod = velocityMethodCounts
                .First(kvp => kvp.Value == velocityMethodCounts.Max(kvp2 => kvp2.Value))
                .Key;

            return GetPointVelocityObservationType(maxVelocityMethod);
        }

        private PointVelocityObservationType GetPointVelocityObservationType(VelocityMethod velocityMethod)
        {
            return VelocityMethodMap.ContainsKey(velocityMethod)
                ? VelocityMethodMap[velocityMethod]
                : PointVelocityObservationType.Unknown;
        }

        private static readonly Dictionary<VelocityMethod, PointVelocityObservationType> VelocityMethodMap =
            new Dictionary<VelocityMethod, PointVelocityObservationType>
            {
                {VelocityMethod.FiveTenths, PointVelocityObservationType.OneAtPointFive},
                {VelocityMethod.SixTenths, PointVelocityObservationType.OneAtPointSix},
                {VelocityMethod.TwoTenthsEightTenths, PointVelocityObservationType.OneAtPointTwoAndPointEight},
                {VelocityMethod.TwoTenthsSixTenthsEightTenths, PointVelocityObservationType.OneAtPointTwoPointSixAndPointEight},
                {VelocityMethod.FivePoint, PointVelocityObservationType.FivePoint},
                {VelocityMethod.SixPoint, PointVelocityObservationType.SixPoint},
            };
    }
}
