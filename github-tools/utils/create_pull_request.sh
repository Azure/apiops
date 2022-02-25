#!/bin/bash

# Get script arguments
PARAMS=""
while (("$#")); do
    [[ $1 == --*=* ]] && set -- "${1%%=*}" "${1#*=}" "${@:2}"
    case "$1" in
    --organization-url)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            organization_url=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --project-name)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            project_name=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --repository-name)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            repository_name=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --pull-request-title)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            pull_request_title=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --branch-name)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            branch_name=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --source-folder-path)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            source_folder_path=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --temporary-branch-name)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            temporary_branch_name=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    --temporary-folder-path)
        if [ -n "$2" ] && [ "${2:0:1}" != "-" ]; then
            temporary_folder_path=$2
            shift 2
        else
            echo "Error: Argument for $1 is missing" >&2
            exit 1
        fi
        ;;
    -*) # unsupported flags
        echo "Error: Unsupported flag $1" >&2
        exit 1
        ;;
    *) # preserve positional arguments
        PARAMS="$PARAMS ""$1"""
        shift
        ;;
    esac
done
eval set -- "$PARAMS"
set -eu -o pipefail

echo "Installing Azure DevOps extension..."
az extension add --name "azure-devops"
az devops configure --defaults organization="${organization_url}" project="${project_name}"

echo "Creating folder ${temporary_folder_path}..."
mkdir -p "${temporary_folder_path}"

echo "Cloning branch ${branch_name}..."
clone_url=$(az repos show --repository "${repository_name}" --query "webUrl" --output tsv)
authenticated_clone_url=${clone_url/\/\////$AZURE_DEVOPS_EXT_PAT@}
git clone --branch "${branch_name}" --depth 1 "${authenticated_clone_url}" "${temporary_folder_path}"

echo "Creating temporary branch ${temporary_branch_name} from ${branch_name}..."
git -C "${temporary_folder_path}" checkout -b "${temporary_branch_name}" "${branch_name}"

echo "Copying source folder ${source_folder_path} contents to temporary folder ${temporary_folder_path}..."
[ -f "${temporary_folder_path}"/pipelines.yaml ] && cp "${temporary_folder_path}"/pipelines.yaml "${source_folder_path}"/pipelines.yaml # Preserve pipelines.yaml file
rm -rfv "${temporary_folder_path}"/*
cp -r "${source_folder_path}"/* "${temporary_folder_path}"/

echo "Validating that changes exist to be published..."
if [[ ! $(git -C "${temporary_folder_path}" status --porcelain | head -1) ]]; then
    echo "No changes exist to be published."
    exit 0
fi

echo "Setting git user information..."
git config --global user.email "azuredevopsagent@azuredevops.com"
git config --global user.name "Azure Devops agent"

echo "Adding changes..."
git -C "${temporary_folder_path}" add --all

echo "Commiting changes..."
git -C "${temporary_folder_path}" commit --message "Initial commit"

echo "Pushing changes..."
git -C "${temporary_folder_path}" push --set-upstream origin "${temporary_branch_name}"

echo "Creating pull request..."
az repos pr create --source-branch "${temporary_branch_name}" --target-branch "${branch_name}" --title "${pull_request_title}" --squash --delete-source-branch "true" --repository "${repository_name}"

echo "Deleting temporary folder contents..."
rm -rf "${temporary_folder_path}/{*,.*}"

echo "Execution complete."