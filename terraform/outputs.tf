output "cluster_name" {
  description = "Name of the provisioned EKS cluster"
  value       = aws_eks_cluster.voting_app.name
}

output "cluster_endpoint" {
  description = "Kubernetes API server endpoint"
  value       = aws_eks_cluster.voting_app.endpoint
}

output "cluster_certificate_authority_data" {
  description = "Base64 encoded certificate data required to communicate with the cluster"
  value       = aws_eks_cluster.voting_app.certificate_authority[0].data
  sensitive   = true
}

output "default_vpc_id" {
  description = "Discovered Default VPC ID"
  value       = data.aws_vpc.default.id
}

output "default_subnet_ids" {
  description = "Selected Default Subnet IDs across distinct Availability Zones"
  value       = local.suitable_subnet_ids
}

output "availability_zones" {
  description = "Distinct Availability Zones selected for EKS"
  value       = local.distinct_azs
}

output "configure_kubectl_command" {
  description = "Command to configure kubectl to access the newly created cluster"
  value       = "aws eks update-kubeconfig --region ${var.aws_region} --name ${aws_eks_cluster.voting_app.name}"
}
