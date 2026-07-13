#!/usr/bin/env bash
# Backfill: invoke the Ingest Lambda to process historical User Activity Report CSVs
# already sitting in the raw bucket. Reuses the live ProcessCsv transform so the
# output is byte-identical to event-driven processing.
#
# Usage:
#   ./scripts/backfill.sh                              # full backfill (all dates)
#   ./scripts/backfill.sh --from 2026-06-20             # from a start date
#   ./scripts/backfill.sh --from 2026-06-20 --to 2026-07-10  # bounded range
#
# Requires: aws CLI, jq, and the IngestLambdaName from `cdk deploy` output.
# Defaults match the POC deployment. Override with AWS_PROFILE, AWS_REGION,
# STACK_NAME, or OUTPUT_FILE when using another environment.

set -euo pipefail

PROFILE="${AWS_PROFILE:-AdministratorAccess-369434902231}"
REGION="${AWS_REGION:-us-east-1}"
STACK_NAME="${STACK_NAME:-KiroInfraStack}"
OUTPUT_FILE="${OUTPUT_FILE:-/tmp/backfill-out.json}"

FROM=""
TO=""

while [[ $# -gt 0 ]]; do
	case "$1" in
	--from)
		FROM="$2"
		shift 2
		;;
	--to)
		TO="$2"
		shift 2
		;;
	*)
		echo "Unknown option: $1"
		exit 1
		;;
	esac
done

for value in "$FROM" "$TO"; do
	if [ -n "$value" ] && [[ ! "$value" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]]; then
		echo "ERROR: Dates must use ISO YYYY-MM-DD format: $value"
		exit 1
	fi
done

if [ -n "$FROM" ] && [ -n "$TO" ] && [[ "$FROM" > "$TO" ]]; then
	echo "ERROR: --from must be on or before --to"
	exit 1
fi

# Resolve the Lambda function name from the CloudFormation stack output.
FN_NAME=$(aws cloudformation describe-stacks \
	--profile "$PROFILE" \
	--region "$REGION" \
	--stack-name "$STACK_NAME" \
	--query "Stacks[0].Outputs[?OutputKey=='IngestLambdaName'].OutputValue" \
	--output text)

if [ -z "$FN_NAME" ] || [ "$FN_NAME" = "None" ]; then
	echo "ERROR: Could not resolve IngestLambdaName from stack $STACK_NAME. Has it been deployed?"
	exit 1
fi

# Build the backfill payload. jq handles optional from/to cleanly.
PAYLOAD=$(jq -n \
	--arg mode "backfill" \
	--arg from "$FROM" \
	--arg to "$TO" \
	'{mode: $mode} +
     (if $from != "" then {from: $from} else {} end) +
     (if $to   != "" then {to:   $to}   else {} end)')

echo "=== Backfill ==="
echo "Function:  $FN_NAME"
echo "Region:    $REGION"
echo "Profile:   $PROFILE"
echo "From:      ${FROM:-unbounded}"
echo "To:        ${TO:-unbounded}"
echo "Payload:   $PAYLOAD"
echo ""

# Invoke asynchronously so Lambda retries and the configured DLQ apply to
# backfill failures exactly as they do to live S3 notifications.
aws lambda invoke \
	--profile "$PROFILE" \
	--region "$REGION" \
	--function-name "$FN_NAME" \
	--invocation-type Event \
	--cli-binary-format raw-in-base64-out \
	--payload "$PAYLOAD" \
	"$OUTPUT_FILE"

echo ""
echo "Backfill accepted for asynchronous processing. Monitor CloudWatch and the ingest DLQ."
echo ""
