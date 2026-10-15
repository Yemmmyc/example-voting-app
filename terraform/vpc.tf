# Discover existing default VPC in the selected AWS region
data "aws_vpc" "default" {
  default = true
}

# Discover existing default subnets within the default VPC
data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }

  filter {
    name   = "default-for-az"
    values = ["true"]
  }
}

# Discover individual subnet attributes to check Availability Zones
data "aws_subnet" "default" {
  for_each = toset(data.aws_subnets.default.ids)
  id       = each.value
}

locals {
  # Exclude AZs known to lack EKS control plane support (such as us-east-1e)
  suitable_subnets = [
    for s in data.aws_subnet.default : s
    if !contains(["us-east-1e"], s.availability_zone)
  ]

  suitable_subnet_ids = [
    for s in local.suitable_subnets : s.id
  ]

  distinct_azs = distinct([
    for s in local.suitable_subnets : s.availability_zone
  ])
}
