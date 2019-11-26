using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using KellermanSoftware.CompareNetObjects;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using Newtonsoft.Json;
using OCM.API.Common.Model;
using OCM.API.Common.Model.Extended;
using OCM.Core.Data;

namespace OCM.API.Common
{
    public class OCMAPIException : Exception
    {
        public OCMAPIException(string message)
            : base(message)
        {
        }
    }
    public struct ValidationResult
    {
        public int? ItemId { get; set; }
        public bool IsValid { get; set; }
        public string Message { get; set; }

        public object Item { get; set; }
    }

    public class POIManager
    {
        public bool LoadUserComments = false;
        public POIManager()
        {
            LoadUserComments = false;
        }

        public POIDetailsCache GetFromCache(int id)
        {
            try
            {
                string cachePath = System.Configuration.ConfigurationManager.AppSettings["CachePath"] + "\\POI_" + id + ".json";
                if (System.IO.File.Exists(cachePath))
                {
                    POIDetailsCache cachedPOI = JsonConvert.DeserializeObject<POIDetailsCache>(System.IO.File.ReadAllText(cachePath));
                    TimeSpan timeSinceCache = DateTime.UtcNow - cachedPOI.DateCached;
                    if (timeSinceCache.TotalDays > 14) return null;
                    return cachedPOI;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        public void CachePOIDetails(Model.ChargePoint poi, List<Model.ChargePoint> nearbyPOI = null)
        {
            try
            {
                string cachePath = System.Configuration.ConfigurationManager.AppSettings["CachePath"] + "\\POI_" + poi.ID + ".json";
                if (!System.IO.File.Exists(cachePath))
                {
                    POIDetailsCache cachedPOI = new POIDetailsCache { POI = poi, DateCached = DateTime.UtcNow, POIListNearby = nearbyPOI };
                    System.IO.File.WriteAllText(cachePath, JsonConvert.SerializeObject(cachedPOI));
                }
            }
            catch (Exception)
            {
                ; ;//caching failed
            }
        }

        public Model.ChargePoint Get(int id)
        {
            return this.Get(id, false);
        }

        public Model.ChargePoint GetCopy(int id, bool resetAddress = true)
        {
            var poi = this.Get(id, false);
            poi.ID = 0;
            poi.MediaItems = null;
            poi.UserComments = null;
            poi.AddressInfo.ID = 0;
            if (resetAddress)
            {
                poi.AddressInfo.Title = "";
                poi.AddressInfo.AddressLine1 = "";
                poi.AddressInfo.AddressLine2 = "";
                poi.AddressInfo.AccessComments = "";
                poi.AddressInfo.GeneralComments = "";
            }
            poi.DataProvider = null;
            poi.DataProviderID = null;
            poi.DataProvidersReference = null;
            poi.DataQualityLevel = null;
            poi.OperatorsReference = null;
            poi.DateCreated = DateTime.UtcNow;
            poi.DateLastConfirmed = null;
            poi.DateLastStatusUpdate = null;

            poi.DatePlanned = null;
            poi.UUID = null;

            foreach (var conn in poi.Connections)
            {
                conn.ID = 0;
                conn.Reference = null;
            }

            return poi;
        }

        public Model.ChargePoint Get(int id, bool includeExtendedInfo, bool allowDiskCache = false, bool allowMirrorDB = false)
        {
            if (allowMirrorDB)
            {
                var p = new CacheProviderMongoDB().GetPOI(id);
                if (p != null)
                {
                    return p;
                }
            }

            try
            {
                var dataModel = new OCMEntities();
                var item = dataModel.ChargePoints.Find(id);

                if (allowDiskCache)
                {
                    var poiCache = GetFromCache(id);
                    if (poiCache != null && poiCache.POI.DateLastStatusUpdate == item.DateLastStatusUpdate)
                    {
                        //found a cached version of POI which is up to date
                        return poiCache.POI;
                    }
                }

                var poi = Model.Extensions.ChargePoint.FromDataModel(item, includeExtendedInfo, includeExtendedInfo, includeExtendedInfo, true);

                if (allowDiskCache && poi != null)
                {
                    //cache results
                    CachePOIDetails(poi);
                }
                return poi;
            }
            catch (Exception)
            {
                //POI not found matching id
                return null;
            }
        }

        /// <summary>
        /// Populates extended properties (reference data etc) of a simple POI from data model, useful for previewing as a fully populated new/edited poi
        /// </summary>
        /// <param name="poi"></param>
        /// <returns></returns>
        public Model.ChargePoint PreviewPopulatedPOIFromModel(Model.ChargePoint poi)
        {
            var dataModel = new OCMEntities();

            var dataPreviewPOI = PopulateChargePoint_SimpleToData(poi, dataModel);

            return Model.Extensions.ChargePoint.FromDataModel(dataPreviewPOI);
        }

        /// <summary>
        /// Check if user can edit given POI with review/approval from another editor
        /// </summary>
        /// <param name="poi"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public static bool CanUserEditPOI(Model.ChargePoint poi, Model.User user)
        {
            if (user == null || poi == null) return false;

            int? countryId = (poi.AddressInfo != null && poi.AddressInfo.Country != null) ? (int?)poi.AddressInfo.Country.ID : null;

            if (UserManager.IsUserAdministrator(user) || UserManager.HasUserPermission(user, null, PermissionLevel.Editor) || (countryId != null && UserManager.HasUserPermission(user, countryId, PermissionLevel.Editor)))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        [DbFunctionAttribute("OCM.Core.Data.OCMEntities.Store", "udf_GetDistanceFromLatLonKM")]
        public static double? GetDistanceFromLatLonKM(double? Latitude1, double? Longitude1, double? Latitude2, double? Longitude2)
        {
            //implements dummy call for entity framework mapping to corresponding SQL function
            throw new NotSupportedException("Direct calls are not supported.");
        }

        /// <summary>
        /// For given query/output settings, return list of charge points. May be a cached response.
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        public List<Model.ChargePoint> GetPOIList(APIRequestParams filter)
        {

            // clone filter settings to remove mutation side effects in callers
            var settings = JsonConvert.DeserializeObject<APIRequestParams>(JsonConvert.SerializeObject(filter));

            var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

            var stopwatch = Stopwatch.StartNew();
            stopwatch.Start();

            string enableCaching = System.Configuration.ConfigurationManager.AppSettings["EnableInMemoryCaching"];
            bool cachingConfigEnabled = bool.Parse(enableCaching);
            if (cachingConfigEnabled == false) settings.EnableCaching = false;

            string cacheKey = settings.HashKey;
            List<Model.ChargePoint> dataList = null;


            bool enableNoSQLCaching = bool.Parse(System.Configuration.ConfigurationManager.AppSettings["EnableNoSQLCaching"]);
            if (enableNoSQLCaching && settings.AllowMirrorDB)
            {
                try
                {
                    var cache = new CacheProviderMongoDB();
                    dataList = cache.GetPOIList(settings);
                }
                catch (Exception)
                {
                    //failed to query mirror db, will now fallback to sql server if dataList is null
                    //mostly likely because cache is being refreshed while querying
                }
            }

            //if dataList is null we didn't get any cache DB results, use SQL DB
            if (dataList == null)
            {
                int maxResults = settings.MaxResults;
                this.LoadUserComments = settings.IncludeComments;
                bool requiresDistance = false;

                if (settings.Latitude != null && settings.Longitude != null)
                {
                    requiresDistance = true;
                    //maxResults = 10000; //TODO find way to prefilter on distance.
                }

                dataList = new List<Model.ChargePoint>();

                var dataModel = new OCMEntities();

                //if distance filter provided in miles, convert to KM before use
                if (settings.DistanceUnit == Model.DistanceUnit.Miles && settings.Distance != null)
                {
                    settings.Distance = GeoManager.ConvertMilesToKM((double)settings.Distance);
                }

                bool filterByConnectionTypes = false;
                bool filterByLevels = false;
                bool filterByOperators = false;
                bool filterByCountries = false;
                bool filterByUsage = false;
                bool filterByStatus = false;
                bool filterByDataProvider = false;
                bool filterByChargePoints = false;

                if (settings.ConnectionTypeIDs != null) { filterByConnectionTypes = true; }
                else { settings.ConnectionTypeIDs = new int[] { -1 }; }

                if (settings.LevelIDs != null) { filterByLevels = true; }
                else { settings.LevelIDs = new int[] { -1 }; }

                if (settings.OperatorIDs != null) { filterByOperators = true; }
                else { settings.OperatorIDs = new int[] { -1 }; }

                if (settings.ChargePointIDs != null) { filterByChargePoints = true; }
                else { settings.ChargePointIDs = new int[] { -1 }; }

                //either filter by named country code or by country id list
                if (settings.CountryCode != null)
                {
                    var filterCountry = dataModel.Countries.FirstOrDefault(c => c.Isocode.ToUpper() == settings.CountryCode.ToUpper());
                    if (filterCountry != null)
                    {
                        filterByCountries = true;
                        settings.CountryIDs = new int[] { filterCountry.Id };
                    }
                    else
                    {
                        filterByCountries = false;
                        settings.CountryIDs = new int[] { -1 };
                    }
                }
                else
                {
                    if (settings.CountryIDs != null && settings.CountryIDs.Any()) { filterByCountries = true; }
                    else { settings.CountryIDs = new int[] { -1 }; }
                }

                if (settings.UsageTypeIDs != null) { filterByUsage = true; }
                else { settings.UsageTypeIDs = new int[] { -1 }; }

                if (settings.StatusTypeIDs != null) { filterByStatus = true; }
                else { settings.StatusTypeIDs = new int[] { -1 }; }

                if (settings.DataProviderIDs != null) { filterByDataProvider = true; }
                else { settings.DataProviderIDs = new int[] { -1 }; }

                if (settings.SubmissionStatusTypeID == -1) settings.SubmissionStatusTypeID = null;

                // compile initial list of locations
                // FIXME: lazy loading properties causes several queries per POI.
                var chargePointList = from c in dataModel.ChargePoints
                                      where
                                          (c.AddressInfo != null)
                                          && ((settings.SubmissionStatusTypeID == null && (c.SubmissionStatusTypeId == null || c.SubmissionStatusTypeId == (int)StandardSubmissionStatusTypes.Imported_Published || c.SubmissionStatusTypeId == (int)StandardSubmissionStatusTypes.Submitted_Published))
                                                || (settings.SubmissionStatusTypeID == 0) //return all regardless of status
                                                || (settings.SubmissionStatusTypeID != null && c.SubmissionStatusTypeId == settings.SubmissionStatusTypeID)
                                                ) //by default return live cps only, otherwise use specific submission statusid
                                          && (c.SubmissionStatusTypeId != (int)StandardSubmissionStatusTypes.Delisted_NotPublicInformation)
                                          && (settings.OperatorName == null || c.Operator.Title == settings.OperatorName)
                                          && (settings.IsOpenData == null || (settings.IsOpenData != null && ((settings.IsOpenData == true && c.DataProvider.IsOpenDataLicensed == true) || (settings.IsOpenData == false && c.DataProvider.IsOpenDataLicensed != true))))
                                          && (settings.DataProviderName == null || c.DataProvider.Title == settings.DataProviderName)
                                          && (settings.LocationTitle == null || c.AddressInfo.Title.Contains(settings.LocationTitle))
                                          && (filterByCountries == false || (filterByCountries == true && settings.CountryIDs.Contains((int)c.AddressInfo.CountryId)))
                                          && (filterByOperators == false || (filterByOperators == true && settings.OperatorIDs.Contains((int)c.OperatorId)))
                                          && (filterByChargePoints == false || (filterByChargePoints == true && settings.ChargePointIDs.Contains((int)c.Id)))
                                          && (filterByUsage == false || (filterByUsage == true && settings.UsageTypeIDs.Contains((int)c.UsageTypeId)))
                                          && (filterByStatus == false || (filterByStatus == true && settings.StatusTypeIDs.Contains((int)c.StatusTypeId)))
                                          && (filterByDataProvider == false || (filterByDataProvider == true && settings.DataProviderIDs.Contains((int)c.DataProviderId)))
                                      select c;

                if (settings.ChangesFromDate != null)
                {
                    chargePointList = chargePointList.Where(c => c.DateLastStatusUpdate >= settings.ChangesFromDate.Value);
                }

                if (settings.CreatedFromDate != null)
                {
                    chargePointList = chargePointList.Where(c => c.DateCreated >= settings.CreatedFromDate.Value);
                }

                //where level of detail is greater than 1 we decide how much to return based on the given level of detail (1-10) Level 10 will return the least amount of data and is suitable for a global overview
                if (settings.LevelOfDetail > 1)
                {
                    //return progressively less matching results (across whole data set) as requested Level Of Detail gets higher
                    // chargePointList = chargePointList.Where(c => c.ID % settings.LevelOfDetail == 0);
                    if (settings.LevelOfDetail > 3)
                    {
                        settings.LevelOfDetail = 1; //highest priority LOD
                    }
                    else
                    {
                        settings.LevelOfDetail = 2; //include next level priority items
                    }
                    chargePointList = chargePointList.Where(c => c.LevelOfDetail <= settings.LevelOfDetail);
                }

                ///////////
                //filter by points along polyline or bounding box
                if (
                    (settings.Polyline != null && settings.Polyline.Any())
                    || (settings.BoundingBox != null && settings.BoundingBox.Any())
                    || (settings.Polygon != null && settings.Polygon.Any())
               )
                {
                    //override lat.long specified in search, use polyline or bounding box instead
                    settings.Latitude = null;
                    settings.Longitude = null;

                    //filter by location within polyline expanded to a polygon (by search distance)

                    IEnumerable<LatLon> searchPolygon = null;
                 
                    if (settings.Polyline != null && settings.Polyline.Any())
                    {
                        if (settings.Distance == null) settings.Distance = 5;
                        searchPolygon = OCM.Core.Util.PolylineEncoder.SearchPolygonFromPolyLine(settings.Polyline, (double)settings.Distance);
                    }

                    if (settings.BoundingBox != null && settings.BoundingBox.Any())
                    {
                        searchPolygon = settings.BoundingBox;
                    }

                    if (settings.Polygon != null && settings.Polygon.Any())
                    {
                        searchPolygon = settings.Polygon;
                    }

                    //invalidate any further use of distance as filter because polyline/bounding box takes precedence
                    settings.Distance = null;
                    requiresDistance = false;

                    int numPoints = searchPolygon.Count();

                    string polygonText = "";
                    foreach (var p in searchPolygon)
                    {
                        polygonText += p.Longitude + " " + p.Latitude;
#if DEBUG
                        System.Diagnostics.Debug.WriteLine(" {lat: " + p.Latitude + ", lng: " + p.Longitude + "},");
#endif
                        polygonText += ", ";
                    }
                    //close polygon
                    var closingPoint = searchPolygon.First();
                    polygonText += closingPoint.Longitude + " " + closingPoint.Latitude;

                    string polygonWKT = "POLYGON((" + polygonText + "))";
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(polygonWKT);
#endif
                    var polygon = new NetTopologySuite.IO.WKTReader(geometryFactory).Read(polygonWKT);
                    chargePointList = chargePointList.Where(q => q.AddressInfo.SpatialPosition.Intersects(polygon));
                }

                //apply connectionInfo filters, all filters must match a distinct connection within the charge point, rather than any filter matching any connectioninfo
                if (settings.ConnectionType != null || settings.MinPowerKW != null || filterByConnectionTypes || filterByLevels)
                {
                    chargePointList = from c in chargePointList
                                      where
                                      c.ConnectionInfoes.Any(conn =>
                                            (settings.ConnectionType == null || (settings.ConnectionType != null && conn.ConnectionType.Title == settings.ConnectionType))
                                            && (settings.MinPowerKW == null || (settings.MinPowerKW != null && conn.PowerKw >= settings.MinPowerKW))
                                            && (filterByConnectionTypes == false || (filterByConnectionTypes == true && settings.ConnectionTypeIDs.Contains(conn.ConnectionTypeId)))
                                            && (filterByLevels == false || (filterByLevels == true && settings.LevelIDs.Contains((int)conn.LevelTypeId)))
                                             )
                                      select c;
                }

                NetTopologySuite.Geometries.Point searchPos = null;

                if (requiresDistance && settings.Latitude != null && settings.Longitude != null)
                {
                    searchPos = geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate((double)settings.Longitude, (double)settings.Latitude));
                }

                //compute/filter by distance (if required)

                var filteredList = from c in chargePointList
                                   where
                                   (requiresDistance == false)
                                   ||
                                   (
                                        requiresDistance == true
                                        && (settings.Latitude != null && settings.Longitude != null)
                                        && (settings.Distance == null ||
                                                (settings.Distance != null && searchPos != null &&
                                                    c.AddressInfo.SpatialPosition.Distance(searchPos) / 1000 < settings.Distance
                                                )
                                        )
                                   )
                                   select new
                                   {
                                       c,
                                       DistanceKM = (requiresDistance ? c.AddressInfo.SpatialPosition.Distance(searchPos) / 1000 : (double?)null)
                                   };

                if (requiresDistance)
                {
                    //if distance was a required output, sort results by distance
                    filteredList = filteredList.OrderBy(d => d.DistanceKM).Take(settings.MaxResults);
                }
                else
                {
                    filteredList = filteredList.OrderByDescending(p => p.c.DateCreated);
                }

                var additionalFilteredList = filteredList.Take(maxResults).ToList();

                stopwatch.Stop();

                System.Diagnostics.Debug.WriteLine("Total query time (ms): " + stopwatch.ElapsedMilliseconds.ToString());

                stopwatch.Restart();

                foreach (var item in additionalFilteredList) //.ToList
                {
                    //note: if include comments is enabled, media items and metadata values are also included
                    Model.ChargePoint c = Model.Extensions.ChargePoint.FromDataModel(item.c, settings.IncludeComments, settings.IncludeComments, settings.IncludeComments, !settings.IsCompactOutput);

                    if (requiresDistance && c.AddressInfo != null)
                    {
                        c.AddressInfo.Distance = item.DistanceKM;

                        if (settings.DistanceUnit == Model.DistanceUnit.Miles && c.AddressInfo.Distance != null)
                        {
                            c.AddressInfo.Distance = GeoManager.ConvertKMToMiles(c.AddressInfo.Distance);
                        }
                        c.AddressInfo.DistanceUnit = settings.DistanceUnit;
                    }

                    if (settings.IsLegacyAPICall && !(settings.APIVersion >= 2))
                    {
                        //for legacy callers, produce artificial list of Charger items
#pragma warning disable 612  //suppress obsolete warning
                        if (c.Chargers == null || c.Chargers.Count == 0)
                        {
                            if (c.Connections != null)
                            {
                                var chargerList = new List<Common.Model.ChargerInfo>();
                                foreach (var con in c.Connections)
                                {
                                    if (con.Level != null)
                                    {
                                        if (!chargerList.Exists(l => l.ChargerType == con.Level))
                                        {
                                            chargerList.Add(new Common.Model.ChargerInfo() { ChargerType = con.Level });
                                        }
                                    }
                                }
                                chargerList = chargerList.Distinct().ToList();
                                c.Chargers = chargerList;
                            }
                        }
                    }

#pragma warning restore 612

                    if (c != null)
                    {
                        dataList.Add(c);
                    }
                }

            }

            System.Diagnostics.Debug.WriteLine("POI List Conversion to simple data model: " + stopwatch.ElapsedMilliseconds + "ms for " + dataList.Count + " results");

            return dataList.Take(settings.MaxResults).ToList();
        }

        /// <summary>
        /// for given charge point, return list of similar charge points based on location/title etc with approx similarity
        /// </summary>
        /// <param name="poi"></param>
        /// <returns></returns>
        public List<Model.ChargePoint> FindSimilar(Model.ChargePoint poi)
        {
            List<Model.ChargePoint> list = new List<Model.ChargePoint>();

            OCMEntities dataModel = new OCMEntities();

            //find similar locations (excluding same cp)
            var similarData = dataModel.ChargePoints.Where(c =>
                        c.Id != poi.ID
                        && c.ParentChargePointId == null //exclude under review and delisted charge points
                        && (c.SubmissionStatusTypeId == null || c.SubmissionStatusTypeId == 100 || c.SubmissionStatusTypeId == 200)
                        && (
                            c.AddressInfoId == poi.AddressInfo.ID
                            ||
                            c.AddressInfo.Postcode == poi.AddressInfo.Postcode
                            ||
                            c.AddressInfo.AddressLine1 == poi.AddressInfo.AddressLine1
                             ||
                            c.AddressInfo.Title == poi.AddressInfo.Title
                            )
                        );

            foreach (var item in similarData)
            {
                Model.ChargePoint c = Model.Extensions.ChargePoint.FromDataModel(item);

                if (c != null)
                {
                    int percentageSimilarity = 0;
                    if (c.AddressInfo.ID == poi.AddressInfo.ID) percentageSimilarity += 75;
                    if (c.AddressInfo.Postcode == poi.AddressInfo.Postcode) percentageSimilarity += 20;
                    if (c.AddressInfo.AddressLine1 == poi.AddressInfo.AddressLine1) percentageSimilarity += 50;
                    if (c.AddressInfo.Title == poi.AddressInfo.Title) percentageSimilarity += 25;
                    if (percentageSimilarity > 100) percentageSimilarity = 99;
                    c.PercentageSimilarity = percentageSimilarity;
                    list.Add(c);
                }
            }
            return list;
        }

        public static ValidationResult IsValid(Model.ChargePoint cp)
        {

            //determine if the basic CP details are valid as a submission or edit
            if (cp.AddressInfo == null) return new ValidationResult { IsValid = false, Message = "AddressInfo is required", Item=cp };

            if (String.IsNullOrEmpty(cp.AddressInfo.Title)) return new ValidationResult { IsValid = false, Message = "AddressInfo requires a Title", Item = cp }; ;

            if (cp.AddressInfo.Country == null && cp.AddressInfo.CountryID == null) return new ValidationResult { IsValid = false, Message = "AddressInfo requires a Country", Item = cp };

            if (cp.AddressInfo.Latitude == 0 && cp.AddressInfo.Longitude == 0) return new ValidationResult { IsValid = false, Message = "AddressInfo requires latitude and longitude", Item = cp };

            double lat = (double)cp.AddressInfo.Latitude;
            double lon = (double)cp.AddressInfo.Longitude;
            if (lat < -90 || lat > 90) return new ValidationResult { IsValid = false, Message = "AddressInfo latitude is out of range", Item = cp };
            if (lon < -180 || lon > 180) return new ValidationResult { IsValid = false, Message = "AddressInfo longitude is out of range", Item = cp }; ;

            if (cp.Connections == null || cp.Connections?.Count == 0) return new ValidationResult { IsValid = false, Message = "One or more Connections required", Item = cp };

            //otherwise, looks basically valid
            return new ValidationResult { IsValid = true, Message = "Passed basic validation", Item = cp };
        }

        private static string GetDisplayName(Type dataType, string fieldName)
        {
            var attr = (DisplayAttribute)dataType.GetProperty(fieldName).GetCustomAttributes(typeof(DisplayAttribute), true).SingleOrDefault();

            if (attr == null)
            {
                var metadataType = (MetadataTypeAttribute)dataType.GetCustomAttributes(typeof(MetadataTypeAttribute), true).FirstOrDefault();
                if (metadataType != null)
                {
                    var property = metadataType.MetadataClassType.GetProperty(fieldName);
                    if (property != null)
                    {
                        attr = (DisplayAttribute)property.GetCustomAttributes(typeof(DisplayAttribute), true).SingleOrDefault();
                    }
                }
            }
            return (attr != null) ? attr.Name : String.Empty;
        }

        public bool HasDifferences(Model.ChargePoint poiA, Model.ChargePoint poiB)
        {
            var diffList = CheckDifferences(poiA, poiB);
            if (diffList.Count == 0)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public List<DiffItem> CheckDifferences(Model.ChargePoint poiA, Model.ChargePoint poiB, bool useObjectCompare = true)
        {
            var diffList = new List<DiffItem>();

            if (poiA == null && poiB == null)
            {
                return diffList;
            }

            if (poiA == null && poiB != null)
            {
                diffList.Add(new DiffItem { Context = "POI", ValueA = null, ValueB = "New" });
                return diffList;
            }

            if (poiB == null && poiA != null)
            {
                diffList.Add(new DiffItem { Context = "POI", ValueA = "New", ValueB = null });
                return diffList;
            }

            if (useObjectCompare)
            {
                var objectComparison = new CompareLogic(new ComparisonConfig { CompareChildren = true, MaxDifferences = 1000 });

                var exclusionList = new string[]
                {
                    "UUID",
                    "MediaItems",
                    "UserComments",
                    "DateCreated",
                    "DateLastStatusUpdate",
                    ".DataProvider.DateLastImported",
                    ".DataProvider.WebsiteURL",
                    ".DataProvider.DataProviderStatusType",
                    ".DataProvider.Comments",
                    ".DataProvider.License",
                    ".StatusType.IsOperational",
                    ".ConnectionType.FormalName",
                    ".ConnectionType.IsDiscontinued",
                    ".ConnectionType.IsObsolete",
                    ".CurrentType.Description",
                    ".Level.Comments",
                    ".AddressInfoID",
                    ".AddressInfo.ID",
                    ".AddressInfo.Distance",
                    ".AddressInfo.DistanceUnit",
                    ".AddressInfo.DistanceUnit.DistanceUnit",
                    ".SubmissionStatus.IsLive",
                    ".OperatorInfo.WebsiteURL",
                    ".OperatorInfo.PhonePrimaryContact",
                    ".OperatorInfo.IsPrivateIndividual",
                    ".OperatorInfo.ContactEmail",
                    ".OperatorInfo.FaultReportEmail",
                    ".UsageType.IsPayAtLocation",
                    ".UsageType.IsMembershipRequired",
                    ".UsageType.IsAccessKeyRequired",
                    ".DataProvider.DataProviderStatusType.ID",
                    ".DataProvider.DataProviderStatusType.Title"
                };
                objectComparison.Config.MembersToIgnore.AddRange(exclusionList);

                var comparisonResult = objectComparison.Compare(poiA, poiB);

                if (!comparisonResult.AreEqual)
                {
                    //clean up differences we want to exclude
                    foreach (var exclusionSuffix in exclusionList)
                    {
                        comparisonResult.Differences.RemoveAll(e => e.PropertyName.EndsWith(exclusionSuffix));
                    }

                    diffList.AddRange(comparisonResult.Differences.Select(difference => new DiffItem { Context = difference.PropertyName, ValueA = difference.Object1Value, ValueB = difference.Object2Value }));

                    //remove items which only vary on null vs ""
                    diffList.RemoveAll(d => (String.IsNullOrWhiteSpace(d.ValueA) || d.ValueA == "(null)") && (String.IsNullOrWhiteSpace(d.ValueB) || d.ValueB == "(null)"));

                    //remove items which are in fact the same
                    diffList.RemoveAll(d => d.ValueA == d.ValueB);
                }
            }
            else
            {
                //perform non-automate diff check
                CompareSimpleRefDataItem(diffList, "Data Provider", poiA.DataProviderID, poiB.DataProviderID, (SimpleReferenceDataType)poiA.DataProvider, (SimpleReferenceDataType)poiB.DataProvider);
                CompareSimpleRefDataItem(diffList, "Network/Operator", poiA.OperatorID, poiB.OperatorID, poiA.OperatorInfo, poiB.OperatorInfo);
                CompareSimpleRefDataItem(diffList, "Operational Status", poiA.StatusTypeID, poiB.StatusTypeID, poiA.StatusType, poiB.StatusType);
                CompareSimpleRefDataItem(diffList, "Usage Type", poiA.UsageTypeID, poiB.UsageTypeID, poiA.UsageType, poiB.UsageType);
                CompareSimpleRefDataItem(diffList, "Submission Status", poiA.SubmissionStatusTypeID, poiB.SubmissionStatusTypeID, poiA.SubmissionStatus, poiB.SubmissionStatus);

                CompareSimpleProperty(diffList, "Data Providers Reference", poiA.DataProvidersReference, poiB.DataProvidersReference);
                CompareSimpleProperty(diffList, "Data Quality Level", poiA.DataQualityLevel, poiB.DataQualityLevel);
                CompareSimpleProperty(diffList, "Number Of Points", poiA.NumberOfPoints, poiB.NumberOfPoints);
                CompareSimpleProperty(diffList, "Usage Cost", poiA.UsageCost, poiB.UsageCost);
                CompareSimpleProperty(diffList, "Address", poiA.GetAddressSummary(false, true), poiB.GetAddressSummary(false, true));
                CompareSimpleProperty(diffList, "General Comments", poiA.GeneralComments, poiB.GeneralComments);
                CompareSimpleProperty(diffList, "Address : Access Comments", poiA.AddressInfo.AccessComments, poiB.AddressInfo.AccessComments);
            }
            return diffList;
        }

        private void CompareSimpleRefDataItem(List<DiffItem> diffList, string displayName, int? ID1, int? ID2, SimpleReferenceDataType refData1, SimpleReferenceDataType refData2)
        {
            if (ID1 != ID2) diffList.Add(new DiffItem { DisplayName = displayName, Context = displayName, ValueA = (refData1 != null ? refData1.Title : ""), ValueB = (refData2 != null ? refData2.Title : "") });
        }

        private void CompareSimpleProperty(List<DiffItem> diffList, string displayName, object val1, object val2)
        {
            //check if object values are different, if so add to diff list
            if (
                (val1 != null && val2 == null && !String.IsNullOrEmpty(val1.ToString())) ||
                (val1 == null && val2 != null && !String.IsNullOrEmpty(val2.ToString())) ||
                (val1 != null && val2 != null && val1.ToString() != val2.ToString())
                )
            {
                diffList.Add(new DiffItem { DisplayName = displayName, Context = displayName, ValueA = (val1 != null ? val1.ToString() : ""), ValueB = (val2 != null ? val2.ToString() : "") });
            }
        }

        /// <summary>
        /// Populate AddressInfo data from settings in a simple AddressInfo object
        /// </summary>
        public Core.Data.AddressInfo PopulateAddressInfo_SimpleToData(Model.AddressInfo simpleAddressInfo, Core.Data.AddressInfo dataAddressInfo, OCMEntities dataModel)
        {
            if (simpleAddressInfo != null && dataAddressInfo == null) dataAddressInfo = new Core.Data.AddressInfo();

            if (simpleAddressInfo != null && dataAddressInfo != null)
            {
                dataAddressInfo.Title = simpleAddressInfo.Title;
                dataAddressInfo.AddressLine1 = simpleAddressInfo.AddressLine1;
                dataAddressInfo.AddressLine2 = simpleAddressInfo.AddressLine2;
                dataAddressInfo.Town = simpleAddressInfo.Town;
                dataAddressInfo.StateOrProvince = simpleAddressInfo.StateOrProvince;
                dataAddressInfo.Postcode = simpleAddressInfo.Postcode;
                if (simpleAddressInfo.CountryID > 0 || (simpleAddressInfo.Country != null && simpleAddressInfo.Country.ID > 0))
                {
                    int countryId = (simpleAddressInfo.CountryID != null ? (int)simpleAddressInfo.CountryID : simpleAddressInfo.Country.ID);
                    dataAddressInfo.Country = dataModel.Countries.FirstOrDefault(c => c.Id == countryId);
                    dataAddressInfo.CountryId = dataAddressInfo.Country.Id;
                }
                dataAddressInfo.Latitude = simpleAddressInfo.Latitude;
                dataAddressInfo.Longitude = simpleAddressInfo.Longitude;
                dataAddressInfo.ContactTelephone1 = simpleAddressInfo.ContactTelephone1;
                dataAddressInfo.ContactTelephone2 = simpleAddressInfo.ContactTelephone2;
                dataAddressInfo.ContactEmail = simpleAddressInfo.ContactEmail;
                dataAddressInfo.AccessComments = simpleAddressInfo.AccessComments;
#pragma warning disable 612 //suppress obsolete warning
                dataAddressInfo.GeneralComments = simpleAddressInfo.GeneralComments;
#pragma warning restore 612 //suppress obsolete warning
                dataAddressInfo.RelatedUrl = simpleAddressInfo.RelatedURL;
            }

            return dataAddressInfo;
        }

        public OCM.Core.Data.ChargePoint PopulateChargePoint_SimpleToData(Model.ChargePoint simplePOI, OCM.Core.Data.OCMEntities dataModel)
        {
            var dataPOI = new OCM.Core.Data.ChargePoint();

            if (simplePOI.ID > 0 && simplePOI.UUID != null) dataPOI = dataModel.ChargePoints.FirstOrDefault(cp => cp.Id == simplePOI.ID && cp.Uuid.ToUpper() == simplePOI.UUID.ToUpper());// dataChargePoint.ID = simpleChargePoint.ID; // dataModel.ChargePoints.FirstOrDefault(c => c.ID == simpleChargePoint.ID);

            if (String.IsNullOrEmpty(dataPOI.Uuid)) dataPOI.Uuid = Guid.NewGuid().ToString().ToUpper();

            //if required, set the parent charge point id
            if (simplePOI.ParentChargePointID != null)
            {
                //dataChargePoint.ParentChargePoint = dataModel.ChargePoints.FirstOrDefault(c=>c.ID==simpleChargePoint.ParentChargePointID);
                dataPOI.ParentChargePointId = simplePOI.ParentChargePointID;
            }
            else
            {
                dataPOI.ParentChargePoint = null;
                dataPOI.ParentChargePointId = null;
            }

            if (simplePOI.DataProviderID > 0 || (simplePOI.DataProvider != null && simplePOI.DataProvider.ID >= 0))
            {
                int providerId = (simplePOI.DataProviderID != null ? (int)simplePOI.DataProviderID : simplePOI.DataProvider.ID);
                try
                {
                    dataPOI.DataProvider = dataModel.DataProviders.First(d => d.Id == providerId);
                    dataPOI.DataProviderId = dataPOI.DataProvider.Id;
                }
                catch (Exception exp)
                {
                    //unknown operator
                    throw new OCMAPIException("Unknown Data Provider Specified:" + providerId + " " + exp.ToString());
                }
            }
            else
            {
                //set to ocm contributor by default
                dataPOI.DataProvider = dataModel.DataProviders.First(d => d.Id == (int)StandardDataProviders.OpenChargeMapContrib);
                dataPOI.DataProviderId = dataPOI.DataProvider.Id;
            }

            dataPOI.DataProvidersReference = simplePOI.DataProvidersReference;

            if (simplePOI.OperatorID >= 1 || (simplePOI.OperatorInfo != null && simplePOI.OperatorInfo.ID >= 0))
            {
                int operatorId = (simplePOI.OperatorID != null ? (int)simplePOI.OperatorID : simplePOI.OperatorInfo.ID);
                try
                {
                    dataPOI.Operator = dataModel.Operators.First(o => o.Id == operatorId);
                    dataPOI.OperatorId = dataPOI.Operator.Id;
                }
                catch (Exception)
                {
                    //unknown operator
                    throw new OCMAPIException("Unknown Network Operator Specified:" + operatorId);
                }
            }
            else
            {
                dataPOI.Operator = null;
                dataPOI.OperatorId = null;
            }

            dataPOI.OperatorsReference = simplePOI.OperatorsReference;

            if (simplePOI.UsageTypeID >= 0 || (simplePOI.UsageType != null && simplePOI.UsageType.ID >= 0))
            {
                int usageTypeId = (simplePOI.UsageTypeID != null ? (int)simplePOI.UsageTypeID : simplePOI.UsageType.ID);
                try
                {
                    dataPOI.UsageType = dataModel.UsageTypes.First(u => u.Id == usageTypeId);
                    dataPOI.UsageTypeId = dataPOI.UsageType.Id;
                }
                catch (Exception)
                {
                    //unknown usage type
                    throw new OCMAPIException("Unknown Usage Type Specified:" + usageTypeId);
                }
            }
            else
            {
                dataPOI.UsageType = null;
                dataPOI.UsageTypeId = null;
            }

            if (dataPOI.AddressInfo == null && simplePOI.AddressInfo.ID > 0)
            {
                var addressInfo = dataModel.ChargePoints.FirstOrDefault(cp => cp.Id == simplePOI.ID).AddressInfo;
                if (addressInfo.Id == simplePOI.AddressInfo.ID) dataPOI.AddressInfo = addressInfo;
            }
            dataPOI.AddressInfo = PopulateAddressInfo_SimpleToData(simplePOI.AddressInfo, dataPOI.AddressInfo, dataModel);

            dataPOI.NumberOfPoints = simplePOI.NumberOfPoints;
            dataPOI.GeneralComments = simplePOI.GeneralComments;
            dataPOI.DatePlanned = simplePOI.DatePlanned;
            dataPOI.UsageCost = simplePOI.UsageCost;

            if (simplePOI.DateLastStatusUpdate != null)
            {
                dataPOI.DateLastStatusUpdate = DateTime.UtcNow;
            }
            else
            {
                dataPOI.DateLastStatusUpdate = DateTime.UtcNow;
            }

            if (simplePOI.DataQualityLevel != null && simplePOI.DataQualityLevel > 0)
            {
                if (simplePOI.DataQualityLevel > 5) simplePOI.DataQualityLevel = 5;
                dataPOI.DataQualityLevel = simplePOI.DataQualityLevel;
            }
            else
            {
                dataPOI.DataQualityLevel = 1;
            }

            if (simplePOI.DateCreated != null)
            {
                dataPOI.DateCreated = simplePOI.DateCreated;
            }
            else
            {
                if (dataPOI.DateCreated == null) dataPOI.DateCreated = DateTime.UtcNow;
            }

            var updateConnectionList = new List<OCM.Core.Data.ConnectionInfo>();
            var deleteList = new List<OCM.Core.Data.ConnectionInfo>();

            if (simplePOI.Connections != null)
            {
                foreach (var c in simplePOI.Connections)
                {
                    var connectionInfo = new Core.Data.ConnectionInfo();

                    //edit existing, if required
                    if (c.ID > 0) connectionInfo = dataModel.ConnectionInfoes.FirstOrDefault(con => con.Id == c.ID && con.ChargePointId == dataPOI.Id);
                    if (connectionInfo == null)
                    {
                        //connection is stale info, start new
                        c.ID = 0;
                        connectionInfo = new Core.Data.ConnectionInfo();
                    }

                    connectionInfo.Reference = c.Reference;
                    connectionInfo.Comments = c.Comments;
                    connectionInfo.Amps = c.Amps;
                    connectionInfo.Voltage = c.Voltage;
                    connectionInfo.Quantity = c.Quantity;
                    connectionInfo.PowerKw = c.PowerKW;

                    if (c.ConnectionTypeID >= 0 || (c.ConnectionType != null && c.ConnectionType.ID >= 0))
                    {
                        int connectionTypeId = (c.ConnectionTypeID != null ? (int)c.ConnectionTypeID : c.ConnectionType.ID);
                        try
                        {
                            connectionInfo.ConnectionType = dataModel.ConnectionTypes.First(ct => ct.Id == connectionTypeId);
                            connectionInfo.ConnectionTypeId = connectionInfo.ConnectionType.Id;
                        }
                        catch (Exception)
                        {
                            throw new OCMAPIException("Unknown Connection Type Specified");
                        }
                    }
                    else
                    {
                        connectionInfo.ConnectionType = null;
                        connectionInfo.ConnectionTypeId = 0;
                    }

                    if (c.LevelID >= 1 || (c.Level != null && c.Level.ID >= 1))
                    {
                        int levelId = (c.LevelID != null ? (int)c.LevelID : c.Level.ID);
                        try
                        {
                            connectionInfo.LevelType = dataModel.ChargerTypes.First(chg => chg.Id == levelId);
                            connectionInfo.LevelTypeId = connectionInfo.LevelTypeId;
                        }
                        catch (Exception)
                        {
                            throw new OCMAPIException("Unknown Charger Level Specified");
                        }
                    }
                    else
                    {
                        connectionInfo.LevelType = null;
                        connectionInfo.LevelTypeId = null;
                    }

                    if (c.CurrentTypeID >= 10 || (c.CurrentType != null && c.CurrentType.ID >= 10))
                    {
                        int currentTypeId = (c.CurrentTypeID != null ? (int)c.CurrentTypeID : c.CurrentType.ID);
                        try
                        {
                            connectionInfo.CurrentType = dataModel.CurrentTypes.First(chg => chg.Id == currentTypeId);
                            connectionInfo.CurrentTypeId = connectionInfo.CurrentType.Id;
                        }
                        catch (Exception)
                        {
                            throw new OCMAPIException("Unknown Current Type Specified");
                        }
                    }
                    else
                    {
                        connectionInfo.CurrentType = null;
                        connectionInfo.CurrentTypeId = null;
                    }

                    if (c.StatusTypeID >= 0 || (c.StatusType != null && c.StatusType.ID >= 0))
                    {
                        int statusTypeId = (c.StatusTypeID != null ? (int)c.StatusTypeID : c.StatusType.ID);
                        try
                        {
                            connectionInfo.StatusType = dataModel.StatusTypes.First(s => s.Id == statusTypeId);
                            connectionInfo.StatusTypeId = connectionInfo.StatusType.Id;
                        }
                        catch (Exception)
                        {
                            throw new OCMAPIException("Unknown Status Type Specified");
                        }
                    }
                    else
                    {
                        connectionInfo.StatusType = null;
                        connectionInfo.StatusTypeId = null;
                    }

                    bool addConnection = false;

                    //detect if connection details are non-blank/unknown before adding
                    if (
                        !String.IsNullOrEmpty(connectionInfo.Reference)
                        || !String.IsNullOrEmpty(connectionInfo.Comments)
                        || connectionInfo.Amps != null
                        || connectionInfo.Voltage != null
                        || connectionInfo.PowerKw != null
                        || (connectionInfo.ConnectionType != null && connectionInfo.ConnectionType.Id > 0)
                        || (connectionInfo.StatusType != null && connectionInfo.StatusTypeId > 0)
                        || (connectionInfo.LevelType != null && connectionInfo.LevelType.Id > 1)
                        || (connectionInfo.CurrentType != null && connectionInfo.CurrentType.Id > 0)
                        || (connectionInfo.Quantity != null && connectionInfo.Quantity > 1)
                    )
                    {
                        addConnection = true;
                        if (connectionInfo.ChargePoint == null) connectionInfo.ChargePoint = dataPOI;
                    }

                    if (addConnection)
                    {
                        //if adding a new connection (not an update) add to model
                        if (c.ID <= 0 || dataPOI.ConnectionInfoes.Count == 0)
                        {
                            dataPOI.ConnectionInfoes.Add(connectionInfo);
                        }
                        //track final list of connections being added/updated  -- will then be used to delete by difference
                        updateConnectionList.Add(connectionInfo);
                    }
                    else
                    {
                        //remove an existing connection no longer required
                        //if (c.ID > 0)
                        {
                            if (connectionInfo.ChargePoint == null) connectionInfo.ChargePoint = dataPOI;
                            deleteList.Add(connectionInfo);
                            //dataChargePoint.Connections.Remove(connectionInfo);
                        }
                    }
                }
            }

            //find existing connections not in updated/added list, add to delete
            if (dataPOI.ConnectionInfoes != null)
            {
                foreach (var con in dataPOI.ConnectionInfoes)
                {
                    if (!updateConnectionList.Contains(con))
                    {
                        if (!deleteList.Contains(con))
                        {
                            deleteList.Add(con);
                        }
                    }
                }
            }

            //finally clean up deleted items
            foreach (var item in deleteList)
            {
                if (item.Id > 0)
                {
                    dataModel.ConnectionInfoes.Remove(item);
                }
            }

            if (dataPOI.MetadataValues == null)
            {
                dataPOI.MetadataValues = new List<OCM.Core.Data.MetadataValue>();
                //add metadata values
            }

            if (simplePOI.MetadataValues != null)
            {
                foreach (var m in simplePOI.MetadataValues)
                {
                    var existingValue = dataPOI.MetadataValues.FirstOrDefault(v => v.Id == m.ID);
                    if (existingValue != null)
                    {
                        existingValue.ItemValue = m.ItemValue;
                    }
                    else
                    {
                        var newValue = new OCM.Core.Data.MetadataValue()
                        {
                            ChargePointId = dataPOI.Id,
                            ItemValue = m.ItemValue,
                            MetadataFieldId = m.MetadataFieldID,
                            MetadataField = dataModel.MetadataFields.FirstOrDefault(f => f.Id == m.MetadataFieldID)
                        };
                        dataPOI.MetadataValues.Add(newValue);
                    }
                }
            }

            //TODO:clean up unused metadata values

            if (simplePOI.StatusTypeID != null || simplePOI.StatusType != null)
            {
                if (simplePOI.StatusTypeID == null && simplePOI.StatusType != null) simplePOI.StatusTypeID = simplePOI.StatusType.ID;
                dataPOI.StatusTypeId = simplePOI.StatusTypeID;
                dataPOI.StatusType = dataModel.StatusTypes.FirstOrDefault(s => s.Id == simplePOI.StatusTypeID);
            }
            else
            {
                dataPOI.StatusType = null;
                dataPOI.StatusTypeId = null;
            }

            if (simplePOI.SubmissionStatusTypeID != null || simplePOI.SubmissionStatus != null)
            {
                if (simplePOI.SubmissionStatusTypeID == null & simplePOI.SubmissionStatus != null) simplePOI.SubmissionStatusTypeID = simplePOI.SubmissionStatus.ID;
                dataPOI.SubmissionStatusType = dataModel.SubmissionStatusTypes.First(s => s.Id == simplePOI.SubmissionStatusTypeID);
                dataPOI.SubmissionStatusTypeId = simplePOI.SubmissionStatusTypeID;
            }
            else
            {
                dataPOI.SubmissionStatusTypeId = null;
                dataPOI.SubmissionStatusType = null; // dataModel.SubmissionStatusTypes.First(s => s.ID == (int)StandardSubmissionStatusTypes.Submitted_UnderReview);
            }

            return dataPOI;
        }

        /// <summary>
        /// used to replace a POI from an external source with a new OCM provided POI, updated POI must still be saved after to store association
        /// </summary>
        /// <param name="oldPOI"></param>
        /// <param name="updatedPOI"></param>
        public int SupersedePOI(OCMEntities dataModel, Model.ChargePoint oldPOI, Model.ChargePoint updatedPOI)
        {
            //When applying an edit to imported or externally provided data:
            //Save old item with new POI ID, Submission Status: Delisted - Superseded By Edit
            //Save edit over old POI ID, with Contributor set to OCM, Parent POI ID set to new (old) POI ID
            //This means that comments/photos/metadata etc for the old POI are preserved against the new POI

            //zero existing ids we want to move to superseded poi (otherwise existing items will be updated)
            oldPOI.ID = 0;
            oldPOI.UUID = null;
            if (oldPOI.AddressInfo != null) oldPOI.AddressInfo.ID = 0;
            if (oldPOI.Connections != null)
            {
                foreach (var connection in oldPOI.Connections)
                {
                    connection.ID = 0;
                }
            }
            oldPOI.SubmissionStatus = null;
            oldPOI.SubmissionStatusTypeID = (int)StandardSubmissionStatusTypes.Delisted_SupersededByUpdate;

            var supersededPOIData = this.PopulateChargePoint_SimpleToData(oldPOI, dataModel);
            supersededPOIData.DateLastStatusUpdate = DateTime.UtcNow;
            dataModel.ChargePoints.Add(supersededPOIData);
            dataModel.SaveChanges();

            //associate updated poi with older parent poi, set OCM as data provider
            updatedPOI.ParentChargePointID = supersededPOIData.Id;
            updatedPOI.DataProvider = null;
            updatedPOI.DataProvidersReference = null;
            updatedPOI.DataProviderID = (int)StandardDataProviders.OpenChargeMapContrib;

            //return new ID for the archived version of the POI
            return supersededPOIData.Id;
        }
    }
}