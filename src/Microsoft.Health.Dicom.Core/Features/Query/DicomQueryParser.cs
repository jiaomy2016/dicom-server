﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dicom;
using EnsureThat;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Dicom.Core.Messages;

namespace Microsoft.Health.Dicom.Core.Features.Query
{
    public class DicomQueryParser : IDicomQueryParser
    {
        private readonly ILogger<DicomQueryParser> _logger;
        private const string IncludeFieldParam = "includefield";
        private const string IncludeFieldValueAll = "all";
        private const string LimitParam = "limit";
        private const string OffsetParam = "offset";
        private const string FuzzyMatchingParam = "fuzzymatching";
        private const string DateTagValuFormat = "yyyyMMdd";
        private const StringComparison QueryParameterComparision = StringComparison.OrdinalIgnoreCase;

        public DicomQueryParser(ILogger<DicomQueryParser> logger)
        {
            EnsureArg.IsNotNull(logger, nameof(logger));
            _logger = logger;
        }

        public DicomQueryExpression Parse(IQueryCollection queryCollection, ResourceType resourceType)
        {
            EnsureArg.IsNotNull(queryCollection, nameof(queryCollection));
            var includeFields = new HashSet<DicomTag>();
            bool fuzzyMatch = false;
            int offset = 0;
            int limit = 0;
            var filterConditions = new List<DicomQueryFilterCondition>();
            bool allValue = false;
            var filterConditionTags = new HashSet<DicomTag>();

            if (queryCollection == null || !queryCollection.Any())
            {
                return new DicomQueryExpression();
            }

            foreach (var queryParam in queryCollection)
            {
                var trimmedKey = queryParam.Key.Trim();
                if (IsQueryParameter(IncludeFieldParam, trimmedKey))
                {
                    ParseIncludeField(queryParam, includeFields, out allValue);
                    continue;
                }

                if (IsQueryParameter(FuzzyMatchingParam, trimmedKey))
                {
                    ParseFuzzyMatching(queryParam, out fuzzyMatch);
                    continue;
                }

                if (IsQueryParameter(LimitParam, trimmedKey))
                {
                    ParseLimit(queryParam, out limit);
                    continue;
                }

                if (IsQueryParameter(OffsetParam, trimmedKey))
                {
                    ParseOffset(queryParam, out offset);
                    continue;
                }

                if (ParseFilterCondition(queryParam, resourceType, out DicomQueryFilterCondition condition))
                {
                    if (filterConditionTags.Contains(condition.DicomTag))
                    {
                        throw new DicomQueryParseException(string.Format(DicomCoreResource.DuplicateQueryParam, queryParam.Key));
                    }

                    filterConditionTags.Add(condition.DicomTag);
                    filterConditions.Add(condition);
                    continue;
                }

                throw new DicomQueryParseException(string.Format(DicomCoreResource.UnkownQueryParameter, queryParam.Key));
            }

            return new DicomQueryExpression(new DicomQueryParameterIncludeField(allValue, includeFields), fuzzyMatch, limit, offset, filterConditions);
        }

        private static bool IsQueryParameter(string expected, string actual)
        {
            return expected.Equals(actual, QueryParameterComparision);
        }

        private void ParseIncludeField(KeyValuePair<string, StringValues> queryParameter, HashSet<DicomTag> includeFields, out bool allValue)
        {
            allValue = false;
            foreach (string value in queryParameter.Value)
            {
                var trimmedValue = value.Trim();
                if (ParseIncludeFieldValueAll(trimmedValue))
                {
                    allValue = true;
                    return;
                }

                if (ParseDicomAttributeId(trimmedValue, out DicomTag dicomTag))
                {
                    includeFields.Add(dicomTag);
                    continue;
                }

                throw new DicomQueryParseException(string.Format(DicomCoreResource.IncludeFieldUnknownAttribute, trimmedValue));
            }
        }

        private bool ParseDicomAttributeId(string attributeId, out DicomTag dicomTag)
        {
            dicomTag = null;

            // Try Keyword match, returns null if not found
            // fo-dicom github bug throwing nullreference https://github.com/fo-dicom/fo-dicom/issues/996
            try
            {
                dicomTag = DicomDictionary.Default[attributeId];
            }
            catch (NullReferenceException e)
            {
                _logger.LogDebug(e, $"DicomDictionary.Default[attributeId] threw exception for {attributeId}");
            }

            if (dicomTag == null)
            {
                dicomTag = ParseDicomTagNumber(attributeId);
            }

            return dicomTag != null;
        }

        private static bool ParseIncludeFieldValueAll(string value)
        {
            return IncludeFieldValueAll.Equals(value, QueryParameterComparision);
        }

        private static void ParseFuzzyMatching(KeyValuePair<string, StringValues> queryParameter, out bool fuzzyMatch)
        {
            fuzzyMatch = false;
            var trimmedValue = queryParameter.Value.FirstOrDefault()?.Trim();
            if (bool.TryParse(trimmedValue, out bool result))
            {
                fuzzyMatch = result;
            }
            else
            {
                throw new DicomQueryParseException(string.Format(DicomCoreResource.InvaludFuzzyMatchValue, trimmedValue));
            }
        }

        private static void ParseOffset(KeyValuePair<string, StringValues> queryParameter, out int offset)
        {
            offset = 0;
            var trimmedValue = queryParameter.Value.FirstOrDefault()?.Trim();
            if (int.TryParse(trimmedValue, out int result))
            {
                offset = result;
            }
            else
            {
                throw new DicomQueryParseException(string.Format(DicomCoreResource.InvalidOffsetValue, trimmedValue));
            }
        }

        private static void ParseLimit(KeyValuePair<string, StringValues> queryParameter, out int limit)
        {
            limit = 0;
            var trimmedValue = queryParameter.Value.FirstOrDefault()?.Trim();
            if (int.TryParse(trimmedValue, out int result))
            {
                if (result > DicomQueryConditionLimit.MaxQueryResultCount)
                {
                    throw new DicomQueryParseException(string.Format(DicomCoreResource.QueryResultCountMaxExceeded, result, DicomQueryConditionLimit.MaxQueryResultCount));
                }

                limit = result;
            }
            else
            {
                throw new DicomQueryParseException(string.Format(DicomCoreResource.InvalidLimitValue, trimmedValue));
            }
        }

        private bool ParseFilterCondition(KeyValuePair<string, StringValues> queryParameter, ResourceType resourceType, out DicomQueryFilterCondition condition)
        {
            condition = null;
            var attributeId = queryParameter.Key.Trim();

            if (!ParseDicomAttributeId(attributeId, out DicomTag dicomTag))
            {
                return false;
            }

            HashSet<DicomTag> supportedQueryTags;
            switch (resourceType)
            {
                case ResourceType.Study:
                    supportedQueryTags = DicomQueryConditionLimit.DicomStudyQueryTagsSupported;
                    break;
                case ResourceType.Series:
                    supportedQueryTags = DicomQueryConditionLimit.DicomSeriesQueryTagsSupported;
                    break;
                case ResourceType.Instance:
                    supportedQueryTags = DicomQueryConditionLimit.DicomInstanceQueryTagsSupported;
                    break;
                default:
                    throw new DicomQueryParseException(DicomCoreResource.QueryInvalidResourceLevel);
            }

            if (!supportedQueryTags.Contains(dicomTag))
            {
                throw new DicomQueryParseException(DicomCoreResource.UnsupportedSearchParameter);
            }

            if (queryParameter.Value.Any())
            {
                var trimmedValue = queryParameter.Value.First().Trim();
                if (dicomTag.DictionaryEntry.ValueRepresentations.FirstOrDefault()?.Code == DicomVRCode.DA)
                {
                    ParseDateTagValue(dicomTag, trimmedValue, out condition);
                }
                else
                {
                    condition = new DicomQuerySingleValueFilterCondition<string>(dicomTag, trimmedValue);
                }
            }

            return condition != null;
        }

        private static void ParseDateTagValue(DicomTag dicomTag, string value, out DicomQueryFilterCondition filterCondition)
        {
            if (DicomQueryConditionLimit.IsValidRangeQueryTag(dicomTag))
            {
                var splitString = value.Split('-');
                if (splitString.Length == 2)
                {
                    string minDate = splitString[0].Trim();
                    string maxDate = splitString[1].Trim();
                    ParseDate(minDate, dicomTag.DictionaryEntry.Keyword);
                    ParseDate(maxDate, dicomTag.DictionaryEntry.Keyword);

                    filterCondition = new DicomQueryRangeValueFilterCondition<string>(dicomTag, minDate, maxDate);
                    return;
                }
            }

            ParseDate(value, dicomTag.DictionaryEntry.Keyword);
            filterCondition = new DicomQuerySingleValueFilterCondition<string>(dicomTag, value);
        }

        private static void ParseDate(string date, string tagKeyword)
        {
            try
            {
                DateTime.ParseExact(date, DateTagValuFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new DicomQueryParseException(string.Format(DicomCoreResource.InvalidDateValue, date, tagKeyword));
            }
         }

        private static DicomTag ParseDicomTagNumber(string s)
        {
            if (s.Length < 8)
            {
                return null;
            }

            if (!ushort.TryParse(s.Substring(0, 4), NumberStyles.HexNumber, null, out ushort group))
            {
                return null;
            }

            if (!ushort.TryParse(s.Substring(4, 4), NumberStyles.HexNumber, null, out ushort element))
            {
                return null;
            }

            var dicomTag = new DicomTag(group, element);
            DicomDictionaryEntry knownTag = DicomDictionary.Default[dicomTag];
            if (knownTag == null || knownTag == DicomDictionary.UnknownTag || knownTag == DicomDictionary.PrivateCreatorTag)
            {
                return null;
            }

            return dicomTag;
        }
    }
}